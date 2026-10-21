namespace Leistd.BackgroundJobs.Recurring;

// 调度器自带的进程内水位兜底。共享存储（如 EF 实现）按这个类型认出它并替换，
// 其他实现仍按"唯一权威存储"冲突处理。
internal interface IProcessLocalRecurringJobStateStore : IRecurringJobStateStore;
