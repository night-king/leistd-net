using Leistd.Ddd.Domain.Entities;
using Leistd.Ddd.Domain.Entities.Auditing;
using Leistd.Ddd.Infrastructure.Persistence;
using Leistd.EventBus.EventHandlers;
using Leistd.EventBus.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.Ddd.Infrastructure.Tests;

/// <summary>同一作用域里两个基座上下文各自保存时，各自的领域事件各发布一次。</summary>
/// <remarks>
/// 领域事件拦截器按 Transient 解析，每个上下文拿到各自的实例；待发布事件按上下文实例暂存。
/// 这条用例钉住"换生命周期不改变按上下文隔离"：事件串到另一个上下文、或被两边各发一遍，都不会报错，只会让处理器少收或多收。
/// </remarks>
public sealed class LocalEventsAcrossContextsTests
{
    // 不开工作单元：事件在各自保存成功后立即发布。工作单元内按阶段分发要经代理工厂织入处理器拦截器，
    // 那条路径由工作单元的阶段用例覆盖，这里只看按上下文隔离
    [Fact]
    public async Task Each_context_publishes_its_own_events_once()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDddInfrastructure();
        services.AddSingleton<NoteRecorder>();
        services.AddSingleton<IEventHandler<NoteWritten>>(sp => sp.GetRequiredService<NoteRecorder>());
        services.AddDbContext<FirstNotesDbContext>(o => o.UseInMemoryDatabase($"first-{Guid.NewGuid():N}"));
        services.AddDbContext<SecondNotesDbContext>(o => o.UseInMemoryDatabase($"second-{Guid.NewGuid():N}"));
        services.AddDddDbContext<FirstNotesDbContext>();
        services.AddDddDbContext<SecondNotesDbContext>();
        await using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var first = scope.ServiceProvider.GetRequiredService<FirstNotesDbContext>();
            var second = scope.ServiceProvider.GetRequiredService<SecondNotesDbContext>();

            first.Notes.Add(Note.Write("first"));
            second.Notes.Add(Note.Write("second"));
            await first.SaveChangesAsync();
            await second.SaveChangesAsync();
            // 再保存一次：已发布的事件不得被重复带出
            await first.SaveChangesAsync();
        }

        Assert.Equal(["first", "second"], provider.GetRequiredService<NoteRecorder>().Texts.Order());
    }

    private sealed class NoteWritten(string text) : LocalEvent
    {
        public string Text { get; } = text;
    }

    private sealed class NoteRecorder : IEventHandler<NoteWritten>
    {
        private readonly List<string> _texts = [];

        public IReadOnlyList<string> Texts
        {
            get { lock (_texts) return [.. _texts]; }
        }

        public Task HandleAsync(NoteWritten @event, CancellationToken cancellationToken = default)
        {
            lock (_texts) _texts.Add(@event.Text);
            return Task.CompletedTask;
        }
    }

    private sealed class Note : FullAuditedEntity<Guid>, IAggregateRoot<Guid>
    {
        public string Text { get; private set; } = string.Empty;

        public static Note Write(string text)
        {
            var note = new Note { Id = Guid.CreateVersion7(), Text = text };
            note.AddLocalEvent(new NoteWritten(text));
            return note;
        }
    }

    private sealed class FirstNotesDbContext(DbContextOptions<FirstNotesDbContext> options, IServiceProvider serviceProvider)
        : BaseDbContext(options, serviceProvider)
    {
        public DbSet<Note> Notes => Set<Note>();

        protected override void ConfigureModel(ModelBuilder modelBuilder) => modelBuilder.Entity<Note>(b => b.HasKey(x => x.Id));
    }

    private sealed class SecondNotesDbContext(DbContextOptions<SecondNotesDbContext> options, IServiceProvider serviceProvider)
        : BaseDbContext(options, serviceProvider)
    {
        public DbSet<Note> Notes => Set<Note>();

        protected override void ConfigureModel(ModelBuilder modelBuilder) => modelBuilder.Entity<Note>(b => b.HasKey(x => x.Id));
    }
}
