/** 分页查询请求基类，对应后端 `Leistd.Data.Paging.PageRequest`。 */
export interface PagedRequestDto {
  /** 跳过的记录数，默认 0。 */
  offset?: number;

  /** 每页记录数，默认 10。 */
  limit?: number;

  /** 排序表达式，形如 `"creationTime desc"`；省略时用该查询的默认排序。 */
  sorting?: string;
}
