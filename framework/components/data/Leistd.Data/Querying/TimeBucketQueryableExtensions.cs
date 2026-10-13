using System.Linq.Expressions;

namespace Leistd.Data.Querying;

/// <summary>按调用方给定的时间桶边界对 <see cref="IQueryable{T}"/> 分组，分组与聚合由持久化 provider 在数据库里完成。</summary>
public static class TimeBucketQueryableExtensions
{
    /// <summary>只保留落在首尾边界之间的元素，并按所在的时间桶分组；组键是桶的下标。</summary>
    /// <remarks>
    /// <para>第 i 个桶是半开区间 [<paramref name="edges"/>[i], <paramref name="edges"/>[i+1])：恰好落在边界上的元素只归后一个桶。
    /// 桶可以不等长（按某个时区切自然日时，夏令时当天的桶是 23 或 25 小时），怎么切由调用方按自己的日历算好；
    /// 本方法只按时刻比较，与时区无关。没有元素的桶不出现在分组里，调用方按下标补零。</para>
    /// <para>桶号写成二分嵌套的条件表达式，366 个桶只有 9 层，不会撞上数据库对 CASE 嵌套层数的限制；
    /// 边界作为查询参数传入，同样桶数的查询共用一份 SQL。</para>
    /// </remarks>
    /// <param name="source">要分组的查询。</param>
    /// <param name="timeSelector">取元素时刻的表达式，须能由持久化 provider 翻译。</param>
    /// <param name="edges">桶边界；至少两个且严格递增，首尾即统计区间。</param>
    /// <typeparam name="T">元素类型。</typeparam>
    /// <returns>按桶下标分组的查询。</returns>
    /// <exception cref="ArgumentException">边界少于两个，或不是严格递增。</exception>
    public static IQueryable<IGrouping<int, T>> GroupByTimeBucket<T>(
        this IQueryable<T> source,
        Expression<Func<T, DateTime>> timeSelector,
        IReadOnlyList<DateTime> edges)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeSelector);
        ArgumentNullException.ThrowIfNull(edges);
        var bucketEdges = new BucketEdges([.. edges]);
        if (bucketEdges.Values.Length < 2)
        {
            throw new ArgumentException("At least two bucket edges are required.", nameof(edges));
        }

        for (var i = 1; i < bucketEdges.Values.Length; i++)
        {
            if (bucketEdges.Values[i] <= bucketEdges.Values[i - 1])
            {
                throw new ArgumentException("Bucket edges must be strictly increasing.", nameof(edges));
            }
        }

        var time = timeSelector.Body;
        var values = Expression.Field(Expression.Constant(bucketEdges), nameof(BucketEdges.Values));
        Expression Edge(int index) => Expression.ArrayIndex(values, Expression.Constant(index));

        Expression BucketOf(int first, int last)
        {
            if (last - first == 1)
            {
                return Expression.Constant(first);
            }

            var middle = (first + last) / 2;
            return Expression.Condition(Expression.LessThan(time, Edge(middle)), BucketOf(first, middle), BucketOf(middle, last));
        }

        var last = bucketEdges.Values.Length - 1;
        var inRange = Expression.Lambda<Func<T, bool>>(
            Expression.AndAlso(Expression.GreaterThanOrEqual(time, Edge(0)), Expression.LessThan(time, Edge(last))),
            timeSelector.Parameters);
        var bucket = Expression.Lambda<Func<T, int>>(BucketOf(0, last), timeSelector.Parameters);

        return source.Where(inRange).GroupBy(bucket);
    }

    // 承载桶边界的闭包对象：表达式引用它的字段，持久化 provider 才会把每个边界作为参数而不是字面量
    private sealed class BucketEdges(DateTime[] values)
    {
        public readonly DateTime[] Values = values;
    }
}
