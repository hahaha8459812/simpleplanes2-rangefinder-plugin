using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SimplePlanes2Rangefinder
{
    /// <summary>
    /// SimplePlanes 2 测距仪插件。
    ///
    /// 做的事：把指定类型、指定名称前缀的零件当作测距仪，从零件位置沿其指向发射线，
    /// 把测出来的距离注册成同名的 Funky Trees 变量。
    ///
    /// 零件判定条件（两者都要满足）：
    ///   1. 零件类型 ID 等于配置的 PartTypeId（默认 Refuel-Probe-1）
    ///   2. 零件名以配置的前缀开头（默认 RF_）
    ///
    /// 变量注册走 Jundroo.Common.Expressions.Context.AddVariable，
    /// 不经过游戏的 VariableSystemScript，所以没有优先级和激活器，
    /// 变量名也不写进作品文件 —— 这些都是该路径的固有性质。
    ///
    /// 关于"只处理本机载具"：实测发现 SetupContext 触发时
    /// AircraftScript.IsPrimaryLocalPlayer 还没被赋值（恒为 false），
    /// 所以注册阶段不能依赖它，只能按载具逐个注册。
    /// 是否只更新本机载具改由 OnlyUpdateLocalPlayer 在更新阶段门控。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class RangefinderPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.codex.simpleplanes2.rangefinder";
        public const string PluginName = "SimplePlanes 2 Rangefinder";
        public const string PluginVersion = "0.1.0";

        private const string AircraftScriptTypeName = "Assets.Scripts.Craft.AircraftScript";
        private const string AddVariableMethodName = "AddVariable";
        private const string IsPrimaryLocalPlayerMemberName = "IsPrimaryLocalPlayer";

        private const float HardcodedDefaultRefreshHz = 10f;
        private const float HardcodedDefaultMaxDistance = 100000f;

        // 安装补丁失败时的重试间隔，用完即放弃（第 PatchRetryDelays.Length + 1 次失败后不再重试）。
        private static readonly float[] PatchRetryDelays = { 2f, 10f, 60f, 60f };

        /// <summary>日志级别。Off 时一条日志都不输出，调用点也不会拼接字符串。</summary>
        private enum PluginLogLevel
        {
            Off = 0,
            Normal = 1,
            Verbose = 2
        }

        /// <summary>一架载具的注册记录。Context 用于识别是不是同一个表达式上下文。</summary>
        private sealed class AircraftEntry
        {
            public object Context;
            public AircraftRangefinderSet Set;
            public Func<bool> LocalPlayerGetter;
            public bool LocalPlayerGetterResolved;
        }

        /// <summary>一次零件扫描的统计，用于诊断为什么某个零件没被识别。</summary>
        private sealed class ScanResult
        {
            public string PartSource;
            public int PartCount;
            public int NameUnreadable;
            public int PrefixMatches;
            public int TypeMatches;
            public int RangefindersRegistered;
            public int SensorsRegistered;

            public int Registered
            {
                get { return RangefindersRegistered + SensorsRegistered; }
            }
        }

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<string> _partNamePrefix;
        private ConfigEntry<string> _partTypeId;
        private ConfigEntry<string> _logLevel;
        private ConfigEntry<string> _sensorNamePrefix;
        private ConfigEntry<float> _defaultRefreshHz;
        private ConfigEntry<float> _defaultMaxDistance;
        private ConfigEntry<bool> _ignoreOwnAircraft;
        private ConfigEntry<bool> _onlyUpdateLocalPlayer;

        private readonly Dictionary<int, AircraftEntry> _entries = new Dictionary<int, AircraftEntry>();
        private readonly List<int> _pruneKeys = new List<int>();
        private readonly List<AircraftEntry> _entrySnapshot = new List<AircraftEntry>();

        // 保护 _entries。SetupContext 的后置补丁可能在线程池线程上触发，而 Update 在主线程枚举同一个字典。
        // _entrySnapshot 只在主线程的 Update 里读写，锁内只负责填充它。
        private readonly object _sync = new object();

        private Harmony _harmony;
        private MethodInfo _addVariableMethod;
        private MethodInfo _getterMethod;
        private Action<object, object> _contextReadyHandler;
        private Type _aircraftType;
        private bool _addVariableLookupFailed;
        private bool _patchInstalled;
        private bool _patchGaveUp;
        private int _patchAttempts;
        private float _nextPatchAttemptTime;
        private bool _wasEnabled = true;
        private bool _hasLoggedFirstContext;
        private bool _aircraftTypeWarningLogged;
        private bool _localPlayerReadFailureLogged;
        private float _effectiveDefaultRefreshHz = HardcodedDefaultRefreshHz;
        private PluginLogLevel _logLevelValue = PluginLogLevel.Normal;
        private int _mainThreadId;
        private float _effectiveDefaultMaxDistance = HardcodedDefaultMaxDistance;

        private void Awake()
        {
            _enabled = Config.Bind("General", "Enabled", true,
                "总开关。关闭时不更新任何变量，并把已注册的变量重新写成各自的\"射线内没有东西\"取值。");

            _partNamePrefix = Config.Bind("General", "PartNamePrefix", "RF_",
                "测距仪零件名必须以此前缀开头，区分大小写。变量名是零件名中第一个 '.' 之前的部分。"
                + "测距仪恒定输出距离，单位米。留空表示不识别测距仪。");

            _sensorNamePrefix = Config.Bind("General", "SensorNamePrefix", "SN_",
                "感应器零件名必须以此前缀开头，区分大小写。变量名是零件名中第一个 '.' 之前的部分。"
                + "感应器与测距仪共用同一个零件类型，靠前缀区分；感应器前缀优先，两个前缀相同时零件按感应器处理。"
                + "感应器默认极值输出，可用零件名的 t 参数改为线性输出。留空表示不识别感应器。");

            _partTypeId = Config.Bind("General", "PartTypeId", "Refuel-Probe-1",
                "被当作测距仪或感应器的零件类型 ID。零件名带前缀、但类型不等于该值的零件会被跳过。");

            _logLevel = Config.Bind("General", "LogLevel", "Normal",
                "日志级别。Off = 完全不输出插件日志；Normal = 输出加载、注册、失败与警告；"
                + "Verbose = 额外输出每个零件的识别过程与线程信息。");

            _defaultRefreshHz = Config.Bind("Rangefinder", "DefaultRefreshHz", HardcodedDefaultRefreshHz,
                "零件名里没有写 r 参数时使用的默认刷新率，单位 Hz。取值范围 0.1 - 120。");

            _defaultMaxDistance = Config.Bind("Rangefinder", "DefaultMaxDistance", HardcodedDefaultMaxDistance,
                "零件名里没有写 m 参数时使用的默认射线长度，单位米。默认 100000，即 100 公里。取值范围 1 - 1000000。");

            _ignoreOwnAircraft = Config.Bind("Rangefinder", "IgnoreOwnAircraft", true,
                "碰撞箱隐藏的全局默认值，作用于零件名里没有写 c 参数的测距仪与感应器。"
                + "true 表示隐藏（排除）属于同一架载具的碰撞箱，避免射线立刻命中自身导致读数恒为 0；"
                + "false 表示不隐藏，射线可以打到自己。本项不影响其他飞机，任何情况下都能测到别的飞机。");

            // 未命中行为固定为"输出该测距仪自己的量程上限"（感应器为 1），不设配置项。
            // 理由：上限是从零件名 m 参数逐个解析的，不是全局常量；做成全局配置会产生歧义。
            // 语义说明见 AircraftRangefinderSet.Measure。

            _onlyUpdateLocalPlayer = Config.Bind("Rangefinder", "OnlyUpdateLocalPlayer", false,
                "只更新本机载具的变量。默认关闭，所有载具各自测各自的。"
                + "注意：变量注册无法按本机过滤（SetupContext 触发时 IsPrimaryLocalPlayer 尚未赋值），"
                + "本项只影响是否持续更新数值；被跳过的载具会写成各自的\"射线内没有东西\"取值。");

            PropertyInfo valueProperty = typeof(RangefinderValueHolder)
                .GetProperty(nameof(RangefinderValueHolder.Value));

            _getterMethod = valueProperty == null ? null : valueProperty.GetGetMethod();

            if (_getterMethod == null)
            {
                LogError("找不到 RangefinderValueHolder.Value 的 getter，插件不会注册任何变量。");
            }

            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _logLevelValue = ParseLogLevel(_logLevel.Value);

            ValidateNumericDefaults();
            WarnAboutPrefixes();

            _contextReadyHandler = OnAircraftContextReady;
            _harmony = new Harmony(PluginGuid);

            TryInstallPatch();

            LogInfo(PluginName + " " + PluginVersion + " 已加载。"
                + "识别条件：类型 " + _partTypeId.Value + "，且零件名以 "
                + _partNamePrefix.Value + "（测距仪）或 " + _sensorNamePrefix.Value + "（感应器）开头。");
        }

        private void Update()
        {
            if (!_patchInstalled)
            {
                if (_patchGaveUp || Time.unscaledTime < _nextPatchAttemptTime)
                {
                    return;
                }

                TryInstallPatch();
                return;
            }

            // prune 与是否启用无关，放在开关判断之前
            PruneDeadEntries();

            if (!_enabled.Value)
            {
                if (_wasEnabled)
                {
                    ResetAllValues();
                    _wasEnabled = false;
                }

                return;
            }

            _wasEnabled = true;

            float now = Time.unscaledTime;

            lock (_sync)
            {
                _entrySnapshot.Clear();
                foreach (KeyValuePair<int, AircraftEntry> pair in _entries)
                {
                    _entrySnapshot.Add(pair.Value);
                }
            }

            bool onlyLocalPlayer = _onlyUpdateLocalPlayer.Value;

            for (int index = 0; index < _entrySnapshot.Count; index++)
            {
                AircraftEntry entry = _entrySnapshot[index];

                if (!entry.Set.IsAlive)
                {
                    // 本帧刚失效的条目会在下一帧的 prune 里移除
                    continue;
                }

                if (onlyLocalPlayer && !IsPrimaryLocalPlayer(entry))
                {
                    entry.Set.ResetValues();
                    continue;
                }

                entry.Set.Update(now);
            }
        }

        private void OnDestroy()
        {
            ResetAllValues();
            VariableContextBinder.Uninstall();

            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
                _harmony = null;
            }

            lock (_sync)
            {
                _entries.Clear();
            }
        }

        private void PruneDeadEntries()
        {
            lock (_sync)
            {
                _pruneKeys.Clear();

                foreach (KeyValuePair<int, AircraftEntry> pair in _entries)
                {
                    if (!pair.Value.Set.IsAlive)
                    {
                        _pruneKeys.Add(pair.Key);
                    }
                }

                for (int index = 0; index < _pruneKeys.Count; index++)
                {
                    _entries.Remove(_pruneKeys[index]);
                }
            }
        }

        private bool LogsEnabled
        {
            get { return _logLevelValue != PluginLogLevel.Off; }
        }

        private bool VerboseEnabled
        {
            get { return _logLevelValue == PluginLogLevel.Verbose; }
        }

        private void LogInfo(string message)
        {
            if (LogsEnabled)
            {
                Logger.LogInfo(message);
            }
        }

        private void LogWarning(string message)
        {
            if (LogsEnabled)
            {
                Logger.LogWarning(message);
            }
        }

        private void LogError(string message)
        {
            if (LogsEnabled)
            {
                Logger.LogError(message);
            }
        }

        private void LogVerbose(string message)
        {
            if (VerboseEnabled)
            {
                LogInfo(message);
            }
        }

        private PluginLogLevel ParseLogLevel(string text)
        {
            if (string.Equals(text, "Off", StringComparison.OrdinalIgnoreCase))
            {
                return PluginLogLevel.Off;
            }

            if (string.Equals(text, "Verbose", StringComparison.OrdinalIgnoreCase))
            {
                return PluginLogLevel.Verbose;
            }

            if (!string.Equals(text, "Normal", StringComparison.OrdinalIgnoreCase))
            {
                // 值无法识别时按 Normal 处理，并直接写一条警告（此时尚不确定用户是否想关日志）
                LogWarning("LogLevel 的值 \"" + text + "\" 无法识别，按 Normal 处理。可用：Off、Normal、Verbose。");
            }

            return PluginLogLevel.Normal;
        }

        private void TryInstallPatch()
        {
            if (_patchInstalled || _patchGaveUp)
            {
                return;
            }

            string failure = null;

            try
            {
                _patchInstalled = VariableContextBinder.Install(_harmony, Logger, _contextReadyHandler, LogsEnabled, out failure);
            }
            catch (Exception exception)
            {
                failure = exception.GetType().Name + " " + exception.Message;
            }

            if (_patchInstalled)
            {
                return;
            }

            _patchAttempts++;

            if (_patchAttempts > PatchRetryDelays.Length)
            {
                _patchGaveUp = true;
                LogError("安装 " + VariableContextBinder.ControlsTypeName + ".SetupContext 补丁失败，"
                    + "已放弃重试：" + failure + "。插件不会注册任何变量。");
                return;
            }

            float delay = PatchRetryDelays[_patchAttempts - 1];
            _nextPatchAttemptTime = Time.unscaledTime + delay;

            LogWarning("安装 " + VariableContextBinder.ControlsTypeName + ".SetupContext 补丁失败："
                + failure + "，" + delay + " 秒后重试（第 " + _patchAttempts + " 次失败）。");
        }

        private void ResetAllValues()
        {
            lock (_sync)
            {
                foreach (KeyValuePair<int, AircraftEntry> pair in _entries)
                {
                    pair.Value.Set.ResetValues();
                }
            }
        }

        private void ValidateNumericDefaults()
        {
            float refreshHz = _defaultRefreshHz.Value;

            if (float.IsNaN(refreshHz) || float.IsInfinity(refreshHz))
            {
                LogWarning("DefaultRefreshHz 的值不是有限数，按 " + HardcodedDefaultRefreshHz + " 处理。");
            }
            else
            {
                _effectiveDefaultRefreshHz = refreshHz;
            }

            float maxDistance = _defaultMaxDistance.Value;

            if (float.IsNaN(maxDistance) || float.IsInfinity(maxDistance))
            {
                LogWarning("DefaultMaxDistance 的值不是有限数，按 " + HardcodedDefaultMaxDistance + " 处理。");
            }
            else
            {
                _effectiveDefaultMaxDistance = maxDistance;
            }
        }

        private void WarnAboutPrefixes()
        {
            string rangefinderPrefix = _partNamePrefix.Value ?? string.Empty;
            string sensorPrefix = _sensorNamePrefix.Value ?? string.Empty;

            if (rangefinderPrefix.Length == 0)
            {
                LogWarning("PartNamePrefix 为空，任何零件都不会被识别为测距仪。");
            }

            if (sensorPrefix.Length == 0)
            {
                LogWarning("SensorNamePrefix 为空，任何零件都不会被识别为感应器。");
            }

            if (rangefinderPrefix.Length > 0 && string.Equals(rangefinderPrefix, sensorPrefix, StringComparison.Ordinal))
            {
                LogWarning("PartNamePrefix 与 SensorNamePrefix 相同（" + rangefinderPrefix
                    + "），这些零件会全部按感应器处理。");
            }
        }

        private Type ResolveAircraftType()
        {
            if (_aircraftType != null)
            {
                return _aircraftType;
            }

            Type resolved = GameReflection.FindType(AircraftScriptTypeName);
            if (resolved != null)
            {
                _aircraftType = resolved;
                return resolved;
            }

            // 找不到不写回字段，下一架载具还会再试一次；警告只打一次
            if (!_aircraftTypeWarningLogged)
            {
                _aircraftTypeWarningLogged = true;
                LogWarning("找不到类型 " + AircraftScriptTypeName
                    + "，无法排除同载具碰撞箱：射线可能立刻命中自身，读数恒为 0。");
            }

            return null;
        }

        private static Func<bool> CreateBoolGetter(object instance, string memberName)
        {
            if (instance == null)
            {
                return null;
            }

            try
            {
                PropertyInfo property = instance.GetType().GetProperty(
                    memberName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (property == null || !property.CanRead || property.PropertyType != typeof(bool))
                {
                    return null;
                }

                MethodInfo getter = property.GetGetMethod(true);
                if (getter == null)
                {
                    return null;
                }

                return (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), instance, getter, false);
            }
            catch
            {
                return null;
            }
        }

        private bool IsPrimaryLocalPlayer(AircraftEntry entry)
        {
            if (!entry.LocalPlayerGetterResolved)
            {
                entry.LocalPlayerGetterResolved = true;
                entry.LocalPlayerGetter = CreateBoolGetter(entry.Set.Aircraft, IsPrimaryLocalPlayerMemberName);
            }

            if (entry.LocalPlayerGetter != null)
            {
                return entry.LocalPlayerGetter();
            }

            // 委托建不起来时退回反射，行为不变，只是每帧多一次装箱
            string failure;
            object value = GameReflection.TryGetMember(entry.Set.Aircraft, IsPrimaryLocalPlayerMemberName, out failure);

            if (failure != null && !_localPlayerReadFailureLogged)
            {
                _localPlayerReadFailureLogged = true;
                LogWarning("读取 " + IsPrimaryLocalPlayerMemberName + " 失败：" + failure
                    + "。OnlyUpdateLocalPlayer 打开时该载具会被当作非本机载具，变量保持在\"射线内没有东西\"取值。");
            }

            return value is bool && (bool)value;
        }

        /// <summary>
        /// 每架载具的表达式上下文就绪时回调（由 VariableContextBinder 的后置补丁触发）。
        /// 该回调可能在线程池线程上执行，写 _entries 时必须持锁。
        /// </summary>
        private void OnAircraftContextReady(object controls, object context)
        {
            if (controls == null || context == null)
            {
                return;
            }

            if (_getterMethod == null)
            {
                return;
            }

            if (VerboseEnabled)
            {
                LogVerbose("表达式上下文就绪：线程 " + Thread.CurrentThread.ManagedThreadId
                    + "（主线程 " + _mainThreadId + "）。");
            }

            object aircraft = GameReflection.GetMember(controls, "_aircraft");
            if (aircraft == null)
            {
                LogWarning("从 AircraftControls 取不到 _aircraft 字段，跳过本次注册。");
                return;
            }

            int aircraftId = GameReflection.GetInstanceId(aircraft);

            AircraftEntry existing;
            lock (_sync)
            {
                if (_entries.TryGetValue(aircraftId, out existing) && existing != null
                    && ReferenceEquals(existing.Context, context)
                    && ReferenceEquals(existing.Set.Aircraft, aircraft)
                    && existing.Set.IsAlive)
                {
                    // 同一个上下文重复触发，已经注册过
                    return;
                }
            }

            AircraftRangefinderSet set = new AircraftRangefinderSet(aircraft, ResolveAircraftType(), WarnFromSet);
            ScanResult result = new ScanResult();
            RegisterRangefinders(aircraft, context, set, result);

            if (!_hasLoggedFirstContext)
            {
                _hasLoggedFirstContext = true;
                LogInfo("SetupContext 首次触发：载具 #" + aircraftId
                    + "，零件来源 " + result.PartSource + "，零件 " + result.PartCount
                    + " 个，名称带前缀 " + result.PrefixMatches
                    + " 个，类型匹配 " + result.TypeMatches
                    + " 个，注册成功 " + result.Registered + " 个，注册线程 "
                    + Thread.CurrentThread.ManagedThreadId + "（主线程 " + _mainThreadId + "）。");
            }

            if (result.Registered > 0)
            {
                AircraftEntry entry = new AircraftEntry();
                entry.Context = context;
                entry.Set = set;

                lock (_sync)
                {
                    _entries[aircraftId] = entry;
                }

                LogInfo("载具 #" + aircraftId + " 注册完成：测距仪变量 "
                    + result.RangefindersRegistered + " 个，感应器变量 " + result.SensorsRegistered + " 个。");
            }
            else if (result.PrefixMatches > 0)
            {
                LogWarning("载具 #" + aircraftId + " 有 " + result.PrefixMatches
                    + " 个零件名带前缀 " + _partNamePrefix.Value + " 或 " + _sensorNamePrefix.Value
                    + "，但一个都没注册成功（其中类型匹配 " + result.TypeMatches
                    + " 个）。请核对零件类型是否为 " + _partTypeId.Value + "。");
            }
            else if (VerboseEnabled)
            {
                LogInfo("载具 #" + aircraftId + " 的表达式上下文已就绪，零件 "
                    + result.PartCount + " 个，没有零件名带前缀 "
                    + _partNamePrefix.Value + " 或 " + _sensorNamePrefix.Value + "。");
            }
        }

        private void WarnFromSet(string message)
        {
            if (LogsEnabled)
            {
                Logger.LogWarning(message);
            }
        }

        /// <summary>
        /// 解析载具的零件列表。
        ///
        /// 优先用 AircraftScript.Aircraft → AircraftData.Assembly → Assembly.Parts：
        /// 游戏自己在 VariableSystemScript.RefreshVariables 里用的就是这条，是数据模型，
        /// 在作品反序列化时就填好，所以 SetupContext 触发时应当可用。
        /// 它不可用时退回 AircraftScript.Parts（实测在 SetupContext 触发时是空的）。
        /// </summary>
        private static IEnumerable ResolvePartList(object aircraft, ScanResult result)
        {
            IEnumerable assemblyParts = null;

            object aircraftData = GameReflection.GetMember(aircraft, "Aircraft");
            if (aircraftData != null)
            {
                object assembly = GameReflection.GetMember(aircraftData, "Assembly");
                if (assembly != null)
                {
                    assemblyParts = GameReflection.GetMember(assembly, "Parts") as IEnumerable;
                }
            }

            IEnumerable directParts = GameReflection.GetMember(aircraft, "Parts") as IEnumerable;

            int assemblyCount = CountOf(assemblyParts);
            int directCount = CountOf(directParts);

            // 优先用 Assembly.Parts。只有它明确为空、而 AircraftScript.Parts 里确实有零件时才退回后者。
            if (assemblyParts != null && (assemblyCount != 0 || directCount <= 0))
            {
                result.PartSource = "Aircraft.Assembly.Parts(" + assemblyCount + ")";
                return assemblyParts;
            }

            if (directParts != null)
            {
                result.PartSource = "AircraftScript.Parts(" + directCount + ")";
                return directParts;
            }

            result.PartSource = "<不可用>";
            return null;
        }

        private static int CountOf(IEnumerable enumerable)
        {
            if (enumerable == null)
            {
                return -1;
            }

            ICollection collection = enumerable as ICollection;

            // 不可计数时返回 -1，与"计数为 0"区分开
            return collection == null ? -1 : collection.Count;
        }

        /// <summary>
        /// 遍历载具零件，找出符合条件的测距仪与感应器并注册变量。
        ///
        /// 两者共用同一个零件类型，靠零件名前缀区分：
        ///   测距仪 RF_ 恒定输出距离
        ///   感应器 SN_ 默认极值输出，可用 t 参数改为线性输出
        ///
        /// 注意零件元素类型是 PartData（不是 PartScript）。
        /// 反射链路：PartData.Name / PartData.PartType.PartTypeId，
        /// 再由 PartData.PartScript 取 Component 与 transform。
        /// </summary>
        private void RegisterRangefinders(object aircraft, object context, AircraftRangefinderSet set, ScanResult result)
        {
            IEnumerable parts = ResolvePartList(aircraft, result);
            if (parts == null)
            {
                LogWarning("拿不到载具的零件列表（Aircraft.Assembly.Parts 与 AircraftScript.Parts 都不可用）。");
                return;
            }

            string rangefinderPrefix = _partNamePrefix.Value;
            string sensorPrefix = _sensorNamePrefix.Value;
            string targetTypeId = _partTypeId.Value;
            float defaultRefreshHz = _effectiveDefaultRefreshHz;
            float defaultMaxDistance = _effectiveDefaultMaxDistance;
            bool defaultHideOwnColliders = _ignoreOwnAircraft.Value;

            HashSet<string> usedVariableNames = new HashSet<string>(StringComparer.Ordinal);
            string firstReadFailure = null;

            foreach (object partData in parts)
            {
                if (partData == null)
                {
                    continue;
                }

                result.PartCount++;

                string readFailure;
                object nameValue = GameReflection.TryGetMember(partData, "Name", out readFailure);
                string partName = nameValue as string;

                if (partName != null)
                {
                    partName = partName.Trim();
                }

                if (string.IsNullOrEmpty(partName))
                {
                    // 读不到与没起名在这里无法从值上区分，靠 readFailure 分辨
                    result.NameUnreadable++;

                    if (readFailure != null && firstReadFailure == null)
                    {
                        firstReadFailure = readFailure;
                    }

                    continue;
                }

                // 条件一：零件名带前缀。感应器前缀优先，两者相同则按感应器处理。
                bool isSensor;
                string matchedPrefix;

                if (!string.IsNullOrEmpty(sensorPrefix) && partName.StartsWith(sensorPrefix, StringComparison.Ordinal))
                {
                    isSensor = true;
                    matchedPrefix = sensorPrefix;
                }
                else if (!string.IsNullOrEmpty(rangefinderPrefix) && partName.StartsWith(rangefinderPrefix, StringComparison.Ordinal))
                {
                    isSensor = false;
                    matchedPrefix = rangefinderPrefix;
                }
                else
                {
                    continue;
                }

                result.PrefixMatches++;

                // 条件二：零件类型是指定类型
                object partType = GameReflection.GetMember(partData, "PartType");
                string partTypeId = GameReflection.GetString(partType, "PartTypeId");

                if (!string.Equals(partTypeId, targetTypeId, StringComparison.Ordinal))
                {
                    LogWarning("零件 \"" + partName + "\" 带前缀 " + matchedPrefix
                        + "，但类型是 " + (partTypeId == null ? "<读不到>" : partTypeId)
                        + "，不等于 " + targetTypeId + "，已跳过。");
                    continue;
                }

                result.TypeMatches++;

                // 取运行期实例。PartData 本身不是 Component，transform 要从它的 PartScript 拿。
                string scriptFailure;
                object partScript = GameReflection.TryGetMember(partData, "PartScript", out scriptFailure);
                Component component = partScript as Component;

                if (component == null)
                {
                    LogWarning("零件 \"" + partName + "\" 类型匹配，但 PartData.PartScript 还不可用"
                        + (string.IsNullOrEmpty(scriptFailure) ? "" : "（" + scriptFailure + "）") + "，已跳过。");
                    continue;
                }

                RangefinderDefinition definition;
                string error;
                string warning;

                if (!RangefinderNameParser.TryParse(
                        partName, matchedPrefix, isSensor,
                        defaultRefreshHz, defaultMaxDistance, defaultHideOwnColliders,
                        out definition, out error, out warning))
                {
                    if (!string.IsNullOrEmpty(error))
                    {
                        LogWarning(error);
                    }

                    continue;
                }

                if (!string.IsNullOrEmpty(warning))
                {
                    LogWarning("零件 \"" + partName + "\"：" + warning);
                }

                if (!usedVariableNames.Add(definition.VariableName))
                {
                    LogWarning("零件 \"" + partName + "\" 解析出的变量名 \""
                        + definition.VariableName + "\" 与本载具上另一个零件重复，已跳过。变量名取自第一个 '.' 之前的部分。");
                    continue;
                }

                float noHitValue = AircraftRangefinderSet.ComputeNoHitValue(
                    definition.OutputMode, definition.MaxDistanceMeters);

                RangefinderValueHolder holder = new RangefinderValueHolder();
                holder.Value = noHitValue;

                if (!TryAddVariable(context, definition.VariableName, holder))
                {
                    usedVariableNames.Remove(definition.VariableName);
                    continue;
                }

                RangefinderBinding binding = new RangefinderBinding();
                binding.OutputMode = definition.OutputMode;
                binding.RefreshIntervalSeconds = definition.RefreshIntervalSeconds;
                binding.MaxDistanceMeters = definition.MaxDistanceMeters;
                binding.NoHitValue = noHitValue;
                binding.HideOwnColliders = definition.HideOwnColliders;
                binding.PartScript = component;
                binding.Holder = holder;
                binding.NextUpdateTime = 0f;
                set.Add(binding);

                if (isSensor)
                {
                    result.SensorsRegistered++;
                }
                else
                {
                    result.RangefindersRegistered++;
                }

                if (LogsEnabled)
                {
                    Logger.LogInfo((isSensor ? "感应器就绪" : "测距仪就绪")
                        + "：零件 \"" + partName + "\" -> 变量 " + definition.VariableName
                        + "，刷新率 " + (1f / definition.RefreshIntervalSeconds).ToString("F2") + " Hz"
                        + "，射线长度 " + definition.MaxDistanceMeters.ToString("F0") + " m"
                        + "，碰撞箱隐藏 " + (definition.HideOwnColliders ? "开" : "关")
                        + "，输出方式 " + DescribeOutputMode(definition.OutputMode)
                        + "，射线方向为零件本地 +Y。");
                }
            }

            if (result.NameUnreadable > 0)
            {
                LogWarning("零件来源 " + result.PartSource + " 中有 " + result.NameUnreadable
                    + " 个零件读不到名字或名字为空，已跳过"
                    + (firstReadFailure == null ? "。" : "（首个失败原因：" + firstReadFailure + "）。"));
            }
        }

        private static string DescribeOutputMode(OutputMode mode)
        {
            switch (mode)
            {
                case OutputMode.Binary:
                    return "极值（没东西 1，有东西 0）";

                case OutputMode.Linear:
                    return "线性（没东西 1，有东西时远处接近 1、近处降到 0）";

                default:
                    return "距离（米）";
            }
        }

        private bool TryAddVariable(object context, string variableName, RangefinderValueHolder holder)
        {
            if (_addVariableMethod == null && !_addVariableLookupFailed)
            {
                _addVariableMethod = context.GetType().GetMethod(
                    AddVariableMethodName,
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(string), typeof(MethodInfo), typeof(object) },
                    null);

                if (_addVariableMethod == null)
                {
                    _addVariableLookupFailed = true;
                    Type contextType = context.GetType();

                    LogWarning("在 " + contextType.FullName + " 上找不到 "
                        + AddVariableMethodName + "(string, MethodInfo, object)，无法注册变量。");
                }
            }

            if (_addVariableMethod == null)
            {
                return false;
            }

            try
            {
                _addVariableMethod.Invoke(context, new object[] { variableName, _getterMethod, holder });
                return true;
            }
            catch (Exception exception)
            {
                Exception cause = exception is TargetInvocationException && exception.InnerException != null
                    ? exception.InnerException
                    : exception;

                LogWarning("注册变量 \"" + variableName + "\" 失败：" + cause.Message);
                return false;
            }
        }
    }
}
