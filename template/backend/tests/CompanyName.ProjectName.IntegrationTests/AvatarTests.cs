#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>头像：上传只收图片、对外给带版本号的地址、图片经专门端点输出，以及编辑表单的原样回写。</summary>
/// <remarks>
/// 服务端不解码图片，只核对文件头与声明的类型一致——这里的"图片"只需要正确的文件头。
/// </remarks>
public sealed class AvatarTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private static string DataUrl(string contentType, byte[] bytes)
        => $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";

    [Fact]
    public async Task Uploaded_avatar_is_served_from_a_versioned_url_with_cache_validation()
    {
        using var user = await CreateUserSessionAsync("avatar_ok");

        using var put = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = DataUrl("image/png", PngBytes) });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var avatar = await ReadAvatarAsync(user.Client);
        Assert.NotNull(avatar);
        Assert.Matches(@"^/api/v1/users/[0-9a-f-]{36}/avatar\?v=", avatar);
        Assert.Contains("?v=", avatar);

        using var image = await user.Client.GetAsync(avatar);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PngBytes, await image.Content.ReadAsByteArrayAsync());

        using var conditional = new HttpRequestMessage(HttpMethod.Get, avatar);
        conditional.Headers.IfNoneMatch.Add(image.Headers.ETag!);
        using var notModified = await user.Client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        // 换一张图，地址里的版本号随之变化，旧缓存不会被误用
        byte[] other = [.. PngBytes, 0x01];
        using var replace = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = DataUrl("image/png", other) });
        Assert.Equal(HttpStatusCode.OK, replace.StatusCode);
        Assert.NotEqual(avatar, await ReadAvatarAsync(user.Client));
    }

    [Theory]
    // 声明是 PNG，内容是 JPEG 的文件头
    [InlineData("image/png", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]
    // 不接受的类型
    [InlineData("image/svg+xml", new byte[] { 0x3C, 0x73, 0x76, 0x67 })]
    public async Task Rejects_content_that_is_not_an_image_or_does_not_match_its_type(string contentType, byte[] bytes)
    {
        using var user = await CreateUserSessionAsync("avatar_bad");

        using var put = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = DataUrl(contentType, bytes) });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Null(await ReadAvatarAsync(user.Client));
    }

    [Fact]
    public async Task Rejects_files_over_the_size_limit()
    {
        using var user = await CreateUserSessionAsync("avatar_big");
        byte[] huge = [.. PngBytes, .. new byte[300 * 1024]];

        using var put = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = DataUrl("image/png", huge) });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    // 本人入口只收图片：外部地址来自外部登录提供方，不是本人随手填的东西
    [Fact]
    public async Task Users_cannot_set_an_external_url_as_their_avatar()
    {
        using var user = await CreateUserSessionAsync("avatar_url");

        using var put = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = "https://example.com/a.png" });

        // 入参校验失败：字段错误落在 avatar 上，不带业务码，头像不变
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        using var body = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.TryGetProperty("code", out _));
        Assert.Contains(body.RootElement.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("field").GetString() == "avatar");
        Assert.Null(await ReadAvatarAsync(user.Client));
    }

    [Fact]
    public async Task Cleared_avatar_endpoint_returns_404()
    {
        using var user = await CreateUserSessionAsync("avatar_clear");
        using var put = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = DataUrl("image/png", PngBytes) });
        var avatar = await ReadAvatarAsync(user.Client);

        using var clear = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = (string?)null });

        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Null(await ReadAvatarAsync(user.Client));
        using var image = await user.Client.GetAsync(avatar);
        Assert.Equal(HttpStatusCode.NotFound, image.StatusCode);
    }

    /// <summary>
    /// 管理员编辑用户时，表单把读到的头像地址原样送回——那表示"没改"，不能因为它不是图片就被拒，
    /// 更不能把存储的图片换成那个地址。
    /// </summary>
    [Fact]
    public async Task Admin_edit_sending_back_the_avatar_url_leaves_it_unchanged()
    {
        using var user = await CreateUserSessionAsync("avatar_admin");
        using var put = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = DataUrl("image/png", PngBytes) });
        var before = await ReadAvatarAsync(user.Client);
        var userId = await ReadUserIdAsync(user.Client);

        using var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var detail = JsonDocument.Parse(await admin.Client.GetStringAsync($"/api/v1/users/{userId}"));
        var root = detail.RootElement;
        Assert.Equal(before, root.GetProperty("avatar").GetString());

        using var update = await admin.Client.PutAsJsonAsync($"/api/v1/users/{userId}", new
        {
            Email = root.GetProperty("email").GetString(),
            DisplayName = "Renamed by admin",
            Avatar = root.GetProperty("avatar").GetString(),
            IsEmailVerified = false
        });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(before, await ReadAvatarAsync(user.Client));
    }

    private async Task<AuthenticatedSession> CreateUserSessionAsync(string prefix)
    {
        var username = $"{prefix}_{Guid.NewGuid():N}"[..30];
        const string password = "AvatarTests!Passw0rd";
        using var admin = await ProjectWebApplicationFactory.LoginAsync(factory, "admin", ProjectWebApplicationFactory.TestAdminPassword);
        using var create = await admin.Client.PostAsJsonAsync("/api/v1/users", new
        {
            Username = username,
            Email = $"{username}@example.test",
            Password = password,
            IsActive = true
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        return await ProjectWebApplicationFactory.LoginAsync(factory, username, password);
    }

    private static async Task<string?> ReadAvatarAsync(HttpClient client)
    {
        using var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        return me.RootElement.TryGetProperty("avatar", out var avatar) ? avatar.GetString() : null;
    }

    private static async Task<Guid> ReadUserIdAsync(HttpClient client)
    {
        using var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        return me.RootElement.GetProperty("id").GetGuid();
    }
}
#endif
