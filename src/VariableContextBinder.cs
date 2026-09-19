using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace SimplePlanes2Rangefinder
{
    /// <summary>
    /// 在 AircraftControls.SetupContext(Context) 上挂后置补丁。
    ///
    /// 为什么选这个方法作为注册点（Game.dll 里 AircraftScript.get_ExpressionContext 的实际流程）：
    ///
    ///     if (_expressionContext == null)
    ///     {
    ///         _expressionContext = new Context(true, new object[] { this });  // 注册 [Exposed] 内建变量
    ///         _expressionContext.GetDeltaTime = ...;
    ///         Controls.SetupContext(_expressionContext);                       // 注册 Pitch/Roll/.../Activate1..8
    ///     }
    ///
    /// 走到这里时：
    ///   1. Context 已建好，内建变量已注册完
    ///   2. 零件级上下文还没派生，任何零件表达式都还没编译
    ///      —— 满足"变量必须早于表达式编译注册"这条硬约束
    ///   3. 补丁能同时拿到 Context（参数）和 AircraftControls 实例（this），
    ///      再由 this._aircraft 拿到 AircraftScript，从而枚举该载具的零件
    ///
    /// 用 __args 而不是按名字绑定参数，避免参数类型匹配问题。
    /// </summary>
    internal static class VariableContextBinder
    {
        public const string ControlsTypeName = "Assets.Scripts.Craft.AircraftControls";

        private const string SetupContextMethodName = "SetupContext";
        private const string ExpressionContextTypeName = "Jundroo.Common.Expressions.Context";

        private static ManualLogSource _logger;
        private static Action<object, object> _onContextReady;
        private static bool _loggingEnabled = true;

        public static bool Install(
            Harmony harmony,
            ManualLogSource logger,
            Action<object, object> onContextReady,
            bool loggingEnabled,
            out string failure)
        {
            failure = null;

            Type controlsType = GameReflection.FindType(ControlsTypeName);
            if (controlsType == null)
            {
                failure = "找不到类型 " + ControlsTypeName;
                return false;
            }

            MethodInfo target = null;
            int candidateCount = 0;
            MethodInfo[] methods = controlsType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            for (int index = 0; index < methods.Length; index++)
            {
                ParameterInfo[] parameters = methods[index].GetParameters();

                if (methods[index].Name != SetupContextMethodName || parameters.Length != 1)
                {
                    continue;
                }

                if (parameters[0].ParameterType.FullName != ExpressionContextTypeName)
                {
                    continue;
                }

                candidateCount++;

                if (target == null)
                {
                    target = methods[index];
                }
            }

            if (target == null)
            {
                failure = "在 " + ControlsTypeName + " 上找不到 "
                    + SetupContextMethodName + "(" + ExpressionContextTypeName + ")";
                return false;
            }

            if (candidateCount > 1 && loggingEnabled && logger != null)
            {
                logger.LogWarning(ControlsTypeName + "." + SetupContextMethodName
                    + " 有 " + candidateCount + " 个签名相同的候选重载，已取第一个：" + target);
            }

            MethodInfo postfix = typeof(VariableContextBinder).GetMethod(
                "SetupContextPostfix", BindingFlags.Static | BindingFlags.NonPublic);

            if (postfix == null)
            {
                failure = "找不到补丁方法 VariableContextBinder.SetupContextPostfix";
                return false;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            }
            catch (Exception exception)
            {
                failure = "Harmony 挂补丁抛异常：" + exception.GetType().Name + " " + exception.Message;
                return false;
            }

            // 只有在补丁真正挂上之后才接管静态字段，失败重试期间不会留下半成品引用
            _logger = logger;
            _onContextReady = onContextReady;
            _loggingEnabled = loggingEnabled;

            if (loggingEnabled && logger != null)
            {
                logger.LogInfo("已挂上 " + ControlsTypeName + "." + SetupContextMethodName + " 的后置补丁。");
            }

            return true;
        }

        /// <summary>解除回调与日志引用，避免静态字段强引用已销毁的插件实例。</summary>
        public static void Uninstall()
        {
            _onContextReady = null;
            _logger = null;
            _loggingEnabled = false;
        }

        private static void SetupContextPostfix(object __instance, object[] __args)
        {
            try
            {
                object context = (__args != null && __args.Length > 0) ? __args[0] : null;
                if (_onContextReady != null)
                {
                    _onContextReady(__instance, context);
                }
            }
            catch (Exception exception)
            {
                if (_loggingEnabled && _logger != null)
                {
                    _logger.LogWarning("处理 SetupContext 时出错：" + exception.Message);
                }
            }
        }
    }
}
