using System.ComponentModel.DataAnnotations;
using System.Linq.Dynamic.Core;
using System.Linq.Dynamic.Core.Exceptions;
using Leistd.Data.Paging;

namespace CompanyName.ProjectName.Application.Shared.Paging;

/// <summary>按调用方提交的排序表达式排序。</summary>
public static class SortingExtensions
{
    /// <summary>以 Dynamic LINQ 解析 <paramref name="sorting"/> 并排序；解析不了时为 <c>sorting</c> 字段的验证错误（400）。</summary>
    /// <remarks>
    /// 只包住解析这一步：解析异常是调用方输入错误；解析成功但查询无法翻译或执行的仍按服务端异常处理（500）。
    /// 库的异常消息回显表达式与成员名，不进响应与日志，因此用固定文案且不保留原异常。
    /// </remarks>
    public static IOrderedQueryable<T> OrderBySorting<T>(this IQueryable<T> source, string sorting)
    {
        try
        {
            return source.OrderBy(sorting);
        }
        catch (ParseException)
        {
            throw new ValidationException(
                new ValidationResult("The sorting expression is not valid.", [nameof(PageRequest.Sorting)]),
                validatingAttribute: null,
                value: null);
        }
    }
}
