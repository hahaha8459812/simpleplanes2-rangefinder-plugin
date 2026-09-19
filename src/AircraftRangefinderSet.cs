using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimplePlanes2Rangefinder
{
    /// <summary>一个测距仪或感应器零件的运行期状态。</summary>
    internal sealed class RangefinderBinding
    {
        public OutputMode OutputMode;
        public float RefreshIntervalSeconds;
        public float MaxDistanceMeters;

        /// <summary>射线内没有东西时该变量取的值，按输出方式在注册时算好。</summary>
        public float NoHitValue;

        public bool HideOwnColliders;
        public Component PartScript;
        public RangefinderValueHolder Holder;
        public float NextUpdateTime;
    }

    /// <summary>
    /// 一架载具上的全部测距仪。每个载具自己的表达式上下文绑定自己的测距仪，
    /// 值也只写进自己的变量，互不干扰。
    /// </summary>
    internal sealed class AircraftRangefinderSet
    {
        private const int InitialHitBufferSize = 32;
        private const int MaxHitBufferSize = 1024;

        /// <summary>输出精度，单位米。</summary>
        private const float OutputPrecision = 0.01f;

        // 复用同一个命中缓冲区，避免每次射线都分配数组。
        // 缓冲被填满时结果顺序无保证、也不保证含最近命中，所以满了就翻倍重测。
        private static RaycastHit[] HitBuffer = new RaycastHit[InitialHitBufferSize];
        private static bool _overflowReported;

        private readonly object _aircraft;
        private readonly Type _aircraftType;
        private readonly Action<string> _warn;
        private readonly List<RangefinderBinding> _bindings = new List<RangefinderBinding>();
        private bool _reportedInvalidPart;

        public AircraftRangefinderSet(object aircraft, Type aircraftType, Action<string> warn)
        {
            _aircraft = aircraft;
            _aircraftType = aircraftType;
            _warn = warn;
        }

        public object Aircraft
        {
            get { return _aircraft; }
        }

        public bool IsAlive
        {
            get { return GameReflection.IsUnityObjectAlive(_aircraft); }
        }

        public void Add(RangefinderBinding binding)
        {
            _bindings.Add(binding);
        }

        /// <summary>
        /// 射线内没有东西时该变量取的值。距离输出与实时未命中读数用同一个取整规则，
        /// 避免"正常未命中"和"插件禁用/零件失效"两种情况下同一个语义给出不同的数。
        /// </summary>
        public static float ComputeNoHitValue(OutputMode mode, float maxDistanceMeters)
        {
            return mode == OutputMode.Distance ? Round(maxDistanceMeters) : 1f;
        }

        /// <summary>
        /// 把本载具全部变量写成各自的"射线内没有东西"取值。
        /// 插件被禁用、该载具被跳过更新、零件失效或插件卸载时调用，避免留下陈旧读数。
        /// </summary>
        public void ResetValues()
        {
            for (int index = 0; index < _bindings.Count; index++)
            {
                RangefinderBinding binding = _bindings[index];
                binding.Holder.Value = binding.NoHitValue;
            }
        }

        public void Update(float now)
        {
            for (int index = _bindings.Count - 1; index >= 0; index--)
            {
                RangefinderBinding binding = _bindings[index];

                if (binding.PartScript == null)
                {
                    // 变量无法从游戏 Context 里撤销，零件失效时先把值复位再移除绑定，
                    // 否则该变量会永久停在最后一次读数。
                    binding.Holder.Value = binding.NoHitValue;
                    _bindings.RemoveAt(index);

                    if (!_reportedInvalidPart)
                    {
                        _reportedInvalidPart = true;
                        Warn("有一个零件的运行期实例已失效，它的变量已被复位并停止更新（本载具只提示一次）。");
                    }

                    continue;
                }

                if (now < binding.NextUpdateTime)
                {
                    continue;
                }

                binding.NextUpdateTime = now + binding.RefreshIntervalSeconds;
                binding.Holder.Value = Measure(binding);
            }
        }

        /// <summary>
        /// 从零件位置沿零件本地 +Y 发一条射线，返回最近命中距离。
        ///
        /// 本地 +Y 是实测确定的探针指向轴：把 Refuel-Probe-1 摆成探针朝上时，
        /// 世界"上方"在零件本地空间里正好是 (0,1,0)，且该零件 attach point 的
        /// 本地法线是 (0,-1,0)，两者精确反向、互相印证。
        /// </summary>
        private float Measure(RangefinderBinding binding)
        {
            // 零件实例在注册期取好，位置与朝向留到测量时（主线程）再读，
            // 避免在表达式上下文可能由线程池线程创建时做 Unity 对象访问。
            Transform partTransform = binding.PartScript.transform;
            Vector3 origin = partTransform.position;
            Vector3 direction = partTransform.up;

            int hitCount = Raycast(origin, direction, binding.MaxDistanceMeters);

            float nearest = float.MaxValue;
            bool found = false;

            for (int index = 0; index < hitCount; index++)
            {
                RaycastHit hit = HitBuffer[index];
                if (hit.collider == null)
                {
                    continue;
                }

                if (binding.HideOwnColliders && IsOwnAircraft(hit.collider))
                {
                    continue;
                }

                if (hit.distance < nearest)
                {
                    nearest = hit.distance;
                    found = true;
                }
            }

            switch (binding.OutputMode)
            {
                case OutputMode.Binary:
                    // 极值输出：射线内没有东西输出 1，有东西输出 0
                    return found ? 0f : binding.NoHitValue;

                case OutputMode.Linear:
                    // 线性输出：没有东西输出 1；
                    // 有东西时输出 距离 / 射线长度，远处接近 1，近处降到 0
                    return found ? Round(nearest / binding.MaxDistanceMeters) : binding.NoHitValue;

                default:
                    // 距离输出：命中给距离，未命中给射线长度
                    return found ? Round(nearest) : binding.NoHitValue;
            }
        }

        /// <summary>
        /// 发射线并返回命中数。缓冲被填满时说明可能有命中没被记下，
        /// 翻倍后重测；到上限仍满则用当前结果，并只报一次警告。
        /// </summary>
        private int Raycast(Vector3 origin, Vector3 direction, float maxDistance)
        {
            while (true)
            {
                int hitCount = Physics.RaycastNonAlloc(
                    origin,
                    direction,
                    HitBuffer,
                    maxDistance,
                    // DefaultRaycastLayers：除 Ignore Raycast 外的所有层。
                    // 不排除 Aircraft 层，因为激光测距仪应当能测到飞机。
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);

                if (hitCount < HitBuffer.Length)
                {
                    return hitCount;
                }

                if (HitBuffer.Length >= MaxHitBufferSize)
                {
                    ReportOverflow();
                    return hitCount;
                }

                int nextSize = HitBuffer.Length * 2;
                HitBuffer = new RaycastHit[nextSize > MaxHitBufferSize ? MaxHitBufferSize : nextSize];
            }
        }

        private void ReportOverflow()
        {
            if (_overflowReported)
            {
                return;
            }

            _overflowReported = true;

            Warn("一次射线测量的命中数达到缓冲上限 " + MaxHitBufferSize
                + "，最近命中可能缺失。请缩短射线长度或减少射线路径上的碰撞箱。");
        }

        private void Warn(string message)
        {
            if (_warn != null)
            {
                _warn(message);
            }
        }

        /// <summary>按输出精度取整，去掉 float 尾数噪声。</summary>
        private static float Round(float value)
        {
            return Mathf.Round(value * (1f / OutputPrecision)) * OutputPrecision;
        }

        private bool IsOwnAircraft(Collider collider)
        {
            Component owner = GameReflection.GetComponentInParent(collider, _aircraftType);
            return owner != null && ReferenceEquals(owner, _aircraft);
        }
    }
}
