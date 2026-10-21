using Leistd.Notifications.Dtos;
using Leistd.Notifications.EntityFrameworkCore.Entities;
using Xunit;

namespace Leistd.Notifications.Tests.EntityFrameworkCore;

// 落库时间必须来自发布方，不能挂在"宿主是否给这个 DbContext 挂了审计拦截器"上。
public class NotificationRecordMappingTests
{
    [Fact]
    public void FromDto_carries_the_creation_time_assigned_by_the_publisher()
    {
        var createdAt = new DateTime(2026, 9, 4, 10, 30, 0, DateTimeKind.Utc);

        var record = NotificationRecord.FromDto(
            new NotificationOutputDto
            {
                // 身份显式给定：Output DTO 不再自带生成 Id 的默认值
                Id = Guid.CreateVersion7().ToString("N"),
                Title = "标题",
                CreationTime = createdAt,
            },
            userId: "user-1");

        Assert.Equal(createdAt, record.CreationTime);
    }

    [Fact]
    public void FromDto_round_trips_through_ToDto()
    {
        var dto = new NotificationOutputDto
        {
            Id = Guid.CreateVersion7().ToString("N"),
            Title = "标题",
            Content = "内容",
            Link = "/orders/1",
            CreationTime = new DateTime(2026, 9, 4, 10, 30, 0, DateTimeKind.Utc),
            RelatedEntityId = "1",
            RelatedEntityType = "Order"
        };

        var round = NotificationRecord.FromDto(dto, userId: "user-1").ToDto();

        Assert.Equal(dto.Title, round.Title);
        Assert.Equal(dto.Content, round.Content);
        Assert.Equal(dto.Link, round.Link);
        Assert.Equal(dto.CreationTime, round.CreationTime);
        Assert.Equal(dto.RelatedEntityId, round.RelatedEntityId);
        Assert.Equal(dto.RelatedEntityType, round.RelatedEntityType);
    }

    // 身份由发布器定案，存储不替它发明：解析不出 Guid 必须抛，而不是悄悄换一个——
    // 换掉会让客户端手里的 ID 与库里的对不上，标记已读永远命不中。
    [Fact]
    public void An_unparsable_id_is_rejected_instead_of_replaced()
    {
        var dto = new NotificationOutputDto
        {
            Id = "not-a-guid",
            Title = "标题",
            CreationTime = DateTime.UnixEpoch,
        };

        var exception = Record.Exception(() => NotificationRecord.FromDto(dto, "user-1"));

        Assert.IsType<ArgumentException>(exception);
    }
}
