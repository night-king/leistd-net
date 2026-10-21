using Xunit;

// Activity / ActivityListener 是**进程级**全局状态：一个测试注册的监听器会让并行运行的
// 其它测试里凭空出现 Activity，于是"无 Activity 那一档"的断言随调度而变。
// 本程序集的断言直接依赖"当前有没有 Activity"，因此关闭并行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
