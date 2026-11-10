using System;
using System.Collections;
using System.Collections.Generic;

namespace FBThrift;

public static class ThriftCollections
{
    public static bool Equals<T>(IEnumerable<T>? first, IEnumerable<T>? second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first == null || second == null) return false;

        if (first is IDictionary dict1 && second is IDictionary dict2)
        {
            if (dict1.Count != dict2.Count) return false;
            foreach (var key in dict1.Keys)
            {
                if (!dict2.Contains(key)) return false;
                if (!EqualsObjects(dict1[key], dict2[key])) return false;
            }
            return true;
        }

        using var e1 = first.GetEnumerator();
        using var e2 = second.GetEnumerator();
        while (true)
        {
            bool has1 = e1.MoveNext();
            bool has2 = e2.MoveNext();
            if (has1 != has2) return false;
            if (!has1) return true;
            if (!EqualsObjects(e1.Current, e2.Current)) return false;
        }
    }

    private static bool EqualsObjects(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (a is IEnumerable ea && b is IEnumerable eb && a is not string && b is not string)
            return Equals(ea, eb);
        return a.Equals(b);
    }
}
