namespace FbThrift.Core.Collections;

/// <summary>
/// Utility methods for comparing Thrift collections.
/// </summary>
public static class ThriftCollections
{
    /// <summary>
    /// Compares two collections for equality using deep comparison.
    /// </summary>
    public static bool Equals<T>(IEnumerable<T>? first, IEnumerable<T>? second)
    {
        if (ReferenceEquals(first, second))
            return true;
        if (first is null || second is null)
            return false;

        if (first is System.Collections.IDictionary dict1 && second is System.Collections.IDictionary dict2)
        {
            if (dict1.Count != dict2.Count)
                return false;
            foreach (System.Collections.DictionaryEntry entry in dict1)
            {
                if (!dict2.Contains(entry.Key))
                    return false;
                var v2 = dict2[entry.Key];
                if (!EqualsObjects(entry.Value, v2))
                    return false;
            }
            return true;
        }

        if (first is System.Collections.ICollection c1 && second is System.Collections.ICollection c2)
        {
            if (c1.Count != c2.Count)
                return false;
        }

        using var e1 = first.GetEnumerator();
        using var e2 = second.GetEnumerator();

        while (true)
        {
            bool has1 = e1.MoveNext();
            bool has2 = e2.MoveNext();
            if (has1 != has2)
                return false;
            if (!has1)
                return true;
            if (!EqualsObjects(e1.Current, e2.Current))
                return false;
        }
    }

    private static bool EqualsObjects(object? a, object? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return false;

        if (a is System.Collections.IEnumerable ea && b is System.Collections.IEnumerable eb &&
            a is not string && b is not string)
        {
            return Equals(ea.Cast<object>(), eb.Cast<object>());
        }

        return a.Equals(b);
    }
}
