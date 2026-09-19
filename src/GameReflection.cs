using System;
using System.Collections.Generic;
using System.Reflection;

namespace SimplePlanes2Rangefinder
{
    /// <summary>
    /// 对游戏类型的最小反射封装。
    ///
    /// 本插件不引用任何游戏程序集，全部通过这里访问，
    /// 目的是让游戏更新导致的失效表现为"功能不工作 + 日志警告"，而不是插件加载失败。
    /// </summary>
    internal static class GameReflection
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // 只缓存找到的结果。未找到不缓存，避免程序集晚加载时被永久负缓存。
        // SetupContext 的回调可能在线程池线程上触发，所以缓存自身要加锁。
        private static readonly Dictionary<string, Type> TypeCache =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        public static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return null;
            }

            lock (TypeCache)
            {
                Type cached;
                if (TypeCache.TryGetValue(fullName, out cached))
                {
                    return cached;
                }
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int index = 0; index < assemblies.Length; index++)
            {
                Type type;

                try
                {
                    type = assemblies[index].GetType(fullName, false);
                }
                catch
                {
                    // 单个程序集读不了就跳过，不影响其余程序集
                    continue;
                }

                if (type != null)
                {
                    // 缓存写入放在 try 之外，也不会被上面那个 catch 吞掉
                    lock (TypeCache)
                    {
                        TypeCache[fullName] = type;
                    }

                    return type;
                }
            }

            return null;
        }

        public static object GetMember(object instance, string memberName)
        {
            string failure;
            return TryGetMember(instance, memberName, out failure);
        }

        /// <summary>
        /// 先找属性再找字段，都支持 public 与 non-public。
        /// 失败时用 failure 说明原因，让调用方能区分"成员不存在"与"取值抛异常"。
        /// 属性存在但不可读时不回退到同名字段，保持与旧实现一致的语义。
        /// </summary>
        public static object TryGetMember(object instance, string memberName, out string failure)
        {
            failure = null;

            if (instance == null)
            {
                failure = "对象为 null";
                return null;
            }

            Type type = instance.GetType();
            PropertyInfo property;
            FieldInfo field;

            try
            {
                property = type.GetProperty(memberName, InstanceFlags);
                field = property == null ? type.GetField(memberName, InstanceFlags) : null;
            }
            catch (Exception exception)
            {
                failure = "在 " + type.FullName + " 上查找成员 " + memberName
                    + " 失败：" + exception.GetType().Name + " " + exception.Message;
                return null;
            }

            if (property != null)
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    failure = type.FullName + "." + memberName + " 不可读（只写属性或索引器）";
                    return null;
                }

                try
                {
                    return property.GetValue(instance, null);
                }
                catch (Exception exception)
                {
                    failure = "读取 " + type.FullName + "." + memberName
                        + " 抛异常：" + exception.GetType().Name + " " + exception.Message;
                    return null;
                }
            }

            if (field != null)
            {
                try
                {
                    return field.GetValue(instance);
                }
                catch (Exception exception)
                {
                    failure = "读取字段 " + type.FullName + "." + memberName
                        + " 抛异常：" + exception.GetType().Name + " " + exception.Message;
                    return null;
                }
            }

            failure = "在 " + type.FullName + " 上找不到成员 " + memberName;
            return null;
        }

        public static bool GetBool(object instance, string memberName)
        {
            object value = GetMember(instance, memberName);
            return value is bool && (bool)value;
        }

        public static string GetString(object instance, string memberName)
        {
            return GetMember(instance, memberName) as string;
        }

        /// <summary>Unity 对象被销毁后，托管引用仍在但底层对象已失效，用这个判断。</summary>
        public static bool IsUnityObjectAlive(object instance)
        {
            UnityEngine.Object unityObject = instance as UnityEngine.Object;
            return unityObject != null;
        }

        /// <summary>取 Unity 对象的实例 ID，用于按载具去重；非 Unity 对象返回 0。</summary>
        public static int GetInstanceId(object instance)
        {
            UnityEngine.Object unityObject = instance as UnityEngine.Object;
            return unityObject == null ? 0 : unityObject.GetInstanceID();
        }

        /// <summary>
        /// 取一个 Unity 对象的第一个匹配组件，失败返回 null。
        /// 用于从 Collider 反查它属于哪架载具。
        /// </summary>
        public static UnityEngine.Component GetComponentInParent(UnityEngine.Component component, Type type)
        {
            if (component == null || type == null)
            {
                return null;
            }

            try
            {
                return component.GetComponentInParent(type);
            }
            catch
            {
                return null;
            }
        }
    }
}
