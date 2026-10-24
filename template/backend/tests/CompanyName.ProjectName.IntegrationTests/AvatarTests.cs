#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
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
        Assert.EndsWith("?v=e7ea26c606a7", avatar);

        using var image = await user.Client.GetAsync(avatar);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PngBytes, await image.Content.ReadAsByteArrayAsync());
        Assert.Equal(CacheControlHeaderValue.Parse("private, max-age=31536000, immutable"), image.Headers.CacheControl);
        Assert.NotNull(image.Headers.ETag);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, avatar);
        conditional.Headers.IfNoneMatch.Add(image.Headers.ETag!);
        using var notModified = await user.Client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(image.Headers.CacheControl, notModified.Headers.CacheControl);

        // 换一张图，地址里的版本号随之变化，旧缓存不会被误用
        byte[] other = [.. PngBytes, 0x01];
        using var replace = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = DataUrl("image/png", other) });
        Assert.Equal(HttpStatusCode.OK, replace.StatusCode);
        Assert.NotEqual(avatar, await ReadAvatarAsync(user.Client));
    }

    [Fact]
    public async Task An_old_version_url_revalidates_until_the_original_avatar_is_restored()
    {
        using var user = await CreateUserSessionAsync("avatar_restore");
        var original = DataUrl("image/png", PngBytes);
        using var upload = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = original });
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var originalUrl = await ReadAvatarAsync(user.Client);
        Assert.NotNull(originalUrl);
        using var originalImage = await user.Client.GetAsync(originalUrl);
        Assert.Equal(HttpStatusCode.OK, originalImage.StatusCode);
        Assert.NotNull(originalImage.Headers.ETag);

        byte[] replacement = [.. PngBytes, 0x01];
        using var replace = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = DataUrl("image/png", replacement) });
        Assert.Equal(HttpStatusCode.OK, replace.StatusCode);
        Assert.NotEqual(originalUrl, await ReadAvatarAsync(user.Client));

        using var staleRequest = new HttpRequestMessage(HttpMethod.Get, originalUrl);
        staleRequest.Headers.IfNoneMatch.Add(originalImage.Headers.ETag);
        using var currentImage = await user.Client.SendAsync(staleRequest);
        Assert.Equal(HttpStatusCode.OK, currentImage.StatusCode);
        Assert.Equal(CacheControlHeaderValue.Parse("private, no-cache"), currentImage.Headers.CacheControl);
        Assert.Equal(replacement, await currentImage.Content.ReadAsByteArrayAsync());
        Assert.NotNull(currentImage.Headers.ETag);
        Assert.NotEqual(originalImage.Headers.ETag, currentImage.Headers.ETag);

        using var validation = new HttpRequestMessage(HttpMethod.Get, originalUrl);
        validation.Headers.IfNoneMatch.Add(currentImage.Headers.ETag);
        using var notModified = await user.Client.SendAsync(validation);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(CacheControlHeaderValue.Parse("private, no-cache"), notModified.Headers.CacheControl);

        using var restore = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = original });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal(originalUrl, await ReadAvatarAsync(user.Client));
        using var restoredImage = await user.Client.GetAsync(originalUrl);
        Assert.Equal(HttpStatusCode.OK, restoredImage.StatusCode);
        Assert.Equal(CacheControlHeaderValue.Parse("private, max-age=31536000, immutable"), restoredImage.Headers.CacheControl);
        Assert.Equal(PngBytes, await restoredImage.Content.ReadAsByteArrayAsync());
        Assert.Equal(originalImage.Headers.ETag, restoredImage.Headers.ETag);
    }

    [Fact]
    public async Task A_cleared_avatar_response_is_not_stored_before_the_same_image_is_uploaded_again()
    {
        using var user = await CreateUserSessionAsync("avatar_reupload");
        var original = DataUrl("image/png", PngBytes);
        using var upload = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = original });
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var originalUrl = await ReadAvatarAsync(user.Client);
        Assert.NotNull(originalUrl);

        using var clear = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = (string?)null });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Null(await ReadAvatarAsync(user.Client));
        using var missing = await user.Client.GetAsync(originalUrl);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(CacheControlHeaderValue.Parse("private, no-store"), missing.Headers.CacheControl);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);

        using var reupload = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = original });
        Assert.Equal(HttpStatusCode.OK, reupload.StatusCode);
        Assert.Equal(originalUrl, await ReadAvatarAsync(user.Client));
        using var restored = await user.Client.GetAsync(originalUrl);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(CacheControlHeaderValue.Parse("private, max-age=31536000, immutable"), restored.Headers.CacheControl);
        Assert.Equal(PngBytes, await restored.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("outdated")]
    [InlineData("uppercase")]
    [InlineData("truncated")]
    public async Task Requests_without_the_exact_current_version_revalidate(string versionKind)
    {
        using var user = await CreateUserSessionAsync("avatar_validate");
        using var upload = await user.Client.PutAsJsonAsync("/api/v1/auth/me/avatar", new { Avatar = DataUrl("image/png", PngBytes) });
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var currentUrl = await ReadAvatarAsync(user.Client);
        Assert.NotNull(currentUrl);
        var path = currentUrl[..currentUrl.IndexOf('?')];
        var requestUrl = versionKind switch
        {
            "missing" => path,
            "empty" => $"{path}?v=",
            "uppercase" => $"{path}?v={currentUrl[(currentUrl.IndexOf('=') + 1)..].ToUpperInvariant()}",
            "truncated" => currentUrl[..^1],
            _ => $"{path}?v=outdated"
        };

        using var image = await user.Client.GetAsync(requestUrl);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(CacheControlHeaderValue.Parse("private, no-cache"), image.Headers.CacheControl);
        Assert.Equal(PngBytes, await image.Content.ReadAsByteArrayAsync());
        Assert.NotNull(image.Headers.ETag);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        conditional.Headers.IfNoneMatch.Add(image.Headers.ETag);
        using var notModified = await user.Client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(image.Headers.ETag, notModified.Headers.ETag);
        Assert.Equal(CacheControlHeaderValue.Parse("private, no-cache"), notModified.Headers.CacheControl);
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
