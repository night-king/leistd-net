namespace Leistd.UnitOfWork.Events;

/// <summary>指定事件处理器的工作单元执行阶段；未标注时为 <see cref="UnitOfWorkPhase.AfterCommit"/>。</summary>
/// <example>
/// <code><![CDATA[
/// [UnitOfWorkEventHandler(Phase = UnitOfWorkPhase.AfterCommit)]
/// public class SendWelcomeEmailHandler : IEventHandler<UserCreatedEvent>
/// {
///     public async Task HandleAsync(UserCreatedEvent @event)
///     {
///         await _emailService.SendWelcomeEmail(@event.User.Email);
///     }
/// }
/// ]]></code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public class UnitOfWorkEventHandlerAttribute(UnitOfWorkPhase phase = UnitOfWorkPhase.AfterCommit) : Attribute
{
    /// <summary>执行阶段，默认 <see cref="UnitOfWorkPhase.AfterCommit"/>。</summary>
    public UnitOfWorkPhase Phase { get; set; } = phase;
}
