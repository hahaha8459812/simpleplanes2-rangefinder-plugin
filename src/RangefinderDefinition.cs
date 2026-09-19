using System;
using System.Globalization;
using System.Text;

namespace SimplePlanes2Rangefinder
{
    /// <summary>一个零件的输出方式。测距仪与感应器各自使用其中一部分。</summary>
    internal enum OutputMode
    {
        /// <summary>输出命中距离，单位米。测距仪专用。</summary>
        Distance,

        /// <summary>极值输出：射线内没有东西输出 1，有东西输出 0。感应器专用。</summary>
        Binary,

        /// <summary>线性输出：没有东西输出 1；有东西时输出 距离 / 感应距离，远处接近 1，近处降到 0。感应器专用。</summary>
        Linear
    }

    /// <summary>
    /// 一个测距仪或感应器零件解析出来的配置。
    /// </summary>
    internal sealed class RangefinderDefinition
    {
        public string VariableName;
        public float RefreshIntervalSeconds;
        public float MaxDistanceMeters;

        /// <summary>
        /// 是否隐藏（排除）属于同一架载具的碰撞箱。
        /// true 时射线不会打到自己；false 时射线可以打到自己。
        /// 由零件名的 c 参数覆盖，未写时取全局配置作为默认。
        /// </summary>
        public bool HideOwnColliders;

        /// <summary>输出方式。由零件名前缀决定是哪一类设备，可由感应器的 t 参数改写。</summary>
        public OutputMode OutputMode;
    }

    /// <summary>
    /// 零件名解析。
    ///
    /// 格式（分隔符为 '.'）：
    ///
    ///     零件名        RF_left.r5.m500.c1
    ///     ├ 变量名      RF_left        第一个 '.' 之前的部分，含前缀
    ///     └ 参数段      r5 . m500 . c1 后续每个 '.' 分隔的记号
    ///
    /// 参数记号（紧凑写法，字母 + 数值）：
    ///
    ///     r<数值>   刷新率，单位 Hz
    ///     m<数值>   射线长度，单位米
    ///     c<0|1>    是否隐藏同载具碰撞箱
    ///     t<1|2>    输出方式，仅感应器可用。t1 极值，t2 线性
    ///
    /// 玩家在 Funky Trees 表达式里只写变量名部分，参数段不参与表达式。
    ///
    /// 未知记号不会导致整个设备失效，只记一条警告并忽略该记号。
    /// </summary>
    internal static class RangefinderNameParser
    {
        private const float MinRefreshHz = 0.1f;
        private const float MaxRefreshHz = 120f;
        private const float MinMaxDistance = 1f;
        private const float MaxMaxDistance = 1000000f;

        public static bool TryParse(
            string partName,
            string prefix,
            bool isSensor,
            float defaultRefreshHz,
            float defaultMaxDistanceMeters,
            bool defaultHideOwnColliders,
            out RangefinderDefinition definition,
            out string error,
            out string warning)
        {
            definition = null;
            error = null;
            warning = null;

            if (string.IsNullOrEmpty(partName))
            {
                return false;
            }

            string trimmed = partName.Trim();

            if (string.IsNullOrEmpty(prefix) ||
                !trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            string[] tokens = trimmed.Split('.');
            string variableName = tokens[0].Trim();

            if (variableName.Length <= prefix.Length)
            {
                error = "零件名 \"" + partName + "\" 只有前缀 " + prefix
                    + "、没有自定义后缀，无法作为变量名";
                return false;
            }

            if (!IsValidVariableName(variableName))
            {
                error = "零件名 \"" + partName + "\" 的变量名 \"" + variableName
                    + "\" 含 Funky Trees 标识符不允许的字符，无法在表达式里写出来";
                return false;
            }

            definition = new RangefinderDefinition();
            definition.VariableName = variableName;
            definition.RefreshIntervalSeconds = HzToInterval(defaultRefreshHz, MinRefreshHz, MaxRefreshHz);
            definition.MaxDistanceMeters = Clamp(defaultMaxDistanceMeters, MinMaxDistance, MaxMaxDistance);
            definition.HideOwnColliders = defaultHideOwnColliders;

            // 感应器默认极值输出，测距仪恒为距离输出。
            definition.OutputMode = isSensor ? OutputMode.Binary : OutputMode.Distance;

            WarningList warnings = new WarningList();

            for (int index = 1; index < tokens.Length; index++)
            {
                string token = tokens[index].Trim();
                if (token.Length == 0)
                {
                    warnings.Add("有一个空参数段，已忽略");
                    continue;
                }

                char kind = token[0];
                string numberText = token.Substring(1);
                float value;

                if (!float.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    warnings.Add("参数 \"" + token + "\" 的数值无法解析，已忽略");
                    continue;
                }

                // float.TryParse 对 "NaN" 与 "Infinity" 都返回 true，放过去会污染读数与刷新间隔
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    warnings.Add("参数 \"" + token + "\" 的数值不是有限数，已忽略");
                    continue;
                }

                switch (kind)
                {
                    case 'r':
                    case 'R':
                        definition.RefreshIntervalSeconds = HzToInterval(value, MinRefreshHz, MaxRefreshHz);
                        break;

                    case 'm':
                    case 'M':
                        definition.MaxDistanceMeters = Clamp(value, MinMaxDistance, MaxMaxDistance);
                        break;

                    case 'c':
                    case 'C':
                        if (value != 0f && value != 1f)
                        {
                            warnings.Add("参数 \"" + token + "\" 只接受 0 或 1，已忽略");
                            break;
                        }

                        definition.HideOwnColliders = value > 0.5f;
                        break;

                    case 't':
                    case 'T':
                        if (!isSensor)
                        {
                            warnings.Add("参数 \"" + token + "\" 只对感应器有效，测距仪已忽略");
                            break;
                        }

                        if (value != 1f && value != 2f)
                        {
                            warnings.Add("参数 \"" + token + "\" 只接受 1 或 2，已忽略");
                            break;
                        }

                        definition.OutputMode = value > 1.5f ? OutputMode.Linear : OutputMode.Binary;
                        break;

                    default:
                        warnings.Add("未知参数 \"" + token
                            + "\"，已忽略（可用：r刷新率、m射线长度、c碰撞箱隐藏"
                            + (isSensor ? "、t输出方式" : "") + "）");
                        break;
                }
            }

            warning = warnings.ToText();
            return true;
        }

        /// <summary>
        /// 变量名必须是游戏词法器认得的标识符。游戏的正则是 \G(?:v:)?[A-z_][A-z_0-9]*，
        /// 其中 [A-z] 是 ASCII 65–122，除字母外还含 [ \ ] ^ _ ` 六个字符。
        /// 这里收紧为 ASCII 字母、数字、下划线，首字符不能是数字：
        /// 被收紧掉的字符虽然游戏能解析，但在表达式里几乎不会有人写，宁可报一条警告。
        /// </summary>
        public static bool IsValidVariableName(string name)
        {
            if (string.IsNullOrEmpty(name) || !IsNameStartChar(name[0]))
            {
                return false;
            }

            for (int index = 1; index < name.Length; index++)
            {
                if (!IsNameChar(name[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsNameStartChar(char value)
        {
            return (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z') || value == '_';
        }

        private static bool IsNameChar(char value)
        {
            return IsNameStartChar(value) || (value >= '0' && value <= '9');
        }

        private static float HzToInterval(float hz, float minHz, float maxHz)
        {
            float clamped = Clamp(hz, minHz, maxHz);
            return 1f / clamped;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value))
            {
                return min;
            }

            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }

        /// <summary>
        /// 收集解析过程中的警告，避免逐条字符串相加；
        /// 超过上限后只保留条数，防止畸形零件名产生任意长的日志文本。
        /// </summary>
        private sealed class WarningList
        {
            private const int MaxMessages = 8;

            private readonly StringBuilder _builder = new StringBuilder();
            private int _count;

            public void Add(string message)
            {
                _count++;

                if (_count > MaxMessages)
                {
                    return;
                }

                if (_builder.Length > 0)
                {
                    _builder.Append('；');
                }

                _builder.Append(message);
            }

            public string ToText()
            {
                if (_count == 0)
                {
                    return null;
                }

                if (_count <= MaxMessages)
                {
                    return _builder.ToString();
                }

                return _builder.ToString() + "；……共 " + _count + " 条";
            }
        }
    }
}
