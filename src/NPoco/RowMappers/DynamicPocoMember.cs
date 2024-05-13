using System.Collections;

namespace NPoco.RowMappers
{
    public class DynamicPocoMember : PocoMember
    {
        public override void SetValue(object target, object value)
        {
            ((IDictionary) target)[Name] = value;
        }

        public override object GetValue(object target)
        {
            var val = ((IDictionary)target).Contains(Name) ? ((IDictionary)target)[Name] : null;
            return val;
        }
    }
}