using System.ComponentModel.DataAnnotations;
using Leistd.Auditing.Abstractions;
using Leistd.Auditing.EntityFrameworkCore.Conventions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Leistd.Auditing.Tests;

public sealed class AuditingEntityConventionTests
{
    [Fact]
    public void Plain_contexts_apply_audit_lengths_and_preserve_explicit_configuration()
    {
        using var context = new AuditContext(new DbContextOptionsBuilder<AuditContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var article = context.Model.FindEntityType(typeof(Article))!;
        Assert.Equal(64, article.FindProperty(nameof(Article.CreatorId))!.GetMaxLength());
        Assert.Equal(64, article.FindProperty(nameof(Article.LastModifierId))!.GetMaxLength());
        Assert.Equal(64, article.FindProperty(nameof(Article.DeleterId))!.GetMaxLength());

        var note = context.Model.FindEntityType(typeof(Note))!;
        Assert.Equal(128, note.FindProperty(nameof(Note.CreatorId))!.GetMaxLength());
        Assert.Equal(96, note.FindProperty(nameof(Note.LastModifierId))!.GetMaxLength());
        Assert.Equal(64, note.FindProperty(nameof(Note.DeleterId))!.GetMaxLength());
        var inherited = context.Model.FindEntityType(typeof(AuditedReply))!;
        Assert.Equal(64, inherited.FindProperty(nameof(Reply.CreatorId))!.GetMaxLength());
    }

    private sealed class AuditContext(DbContextOptions<AuditContext> options) : DbContext(options)
    {
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
            => configurationBuilder.Conventions.Add(_ => new AuditingEntityConvention());

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Article>();
            modelBuilder.Entity<Note>().Property(x => x.CreatorId).HasMaxLength(128);
            modelBuilder.Entity<Reply>();
            modelBuilder.Entity<AuditedReply>();
        }
    }

    private class Article : IFullAuditedObject
    {
        public Guid Id { get; set; }
        public DateTime CreationTime { get; set; }
        public string? CreatorId { get; set; }
        public DateTime? LastModificationTime { get; set; }
        public virtual string? LastModifierId { get; set; }
        public bool IsDeleted { get; set; }
        public DateTime? DeletionTime { get; set; }
        public string? DeleterId { get; set; }
    }

    private sealed class Note : IFullAuditedObject
    {
        public Guid Id { get; set; }
        public DateTime CreationTime { get; set; }
        public string? CreatorId { get; set; }
        public DateTime? LastModificationTime { get; set; }
        [MaxLength(96)]
        public string? LastModifierId { get; set; }
        public bool IsDeleted { get; set; }
        public DateTime? DeletionTime { get; set; }
        public string? DeleterId { get; set; }
    }

    private class Reply
    {
        public Guid Id { get; set; }
        public string? CreatorId { get; set; }
    }

    private sealed class AuditedReply : Reply, ICreationAuditedObject
    {
        public DateTime CreationTime { get; set; }
    }
}
