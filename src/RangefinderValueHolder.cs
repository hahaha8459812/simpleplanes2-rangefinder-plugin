namespace SimplePlanes2Rangefinder
{
    /// <summary>
    /// 一个测距变量的取值持有对象，每个测距仪零件一个实例。
    ///
    /// 为什么需要单独的实例：注册变量用的是
    /// Context.AddVariable(变量名, getter方法, 持有对象)，
    /// 它只接受一个 MethodInfo 加一个 object。同一个 getter 方法可以配多个实例，
    /// 于是每个变量名都有自己的持有对象，表达式求值时读到的就是这个对象当前的 Value。
    ///
    /// 必须是实例属性（不能是静态方法）：游戏自身注册内建变量时用的就是
    /// 实例 getter + 实例对象的组合，静态方法配 null 实例没有验证过。
    /// </summary>
    public sealed class RangefinderValueHolder
    {
        private float _value;

        public float Value
        {
            get { return _value; }
            set { _value = value; }
        }
    }
}
