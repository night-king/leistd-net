#if (SpaFrontend)
# -----------------------------------
# Stage 1: Build Frontend (Angular)
# -----------------------------------
# 取当前 Node LTS；Angular CLI 要求 Node ^22.22.3、^24.15.0 或 >=26。
FROM node:24-alpine AS frontend-build
WORKDIR /app

# 锁定文件是前端构建的唯一依赖基线。
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --ignore-scripts --no-audit

# 复制源代码并构建；前端以相对路径访问同源 API，不注入独立 API 地址
COPY frontend/ ./
RUN npm run build -- --configuration=production

#endif
# -----------------------------------
# Build Backend (.NET)
# -----------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src

# 复制后端代码（.dockerignore 已排除 bin/obj）
# 注意：Leistd.* 框架以 NuGet 包形式引用（见 backend/Directory.Packages.props），
# publish 期间从 NuGet 源还原，无需将 framework 源码拷入镜像。
# 使用私有 NuGet 源时把 NuGet.Config 放在 backend/ 下：构建上下文只拷入 backend/，
# 项目根目录的 NuGet.Config 不会进入镜像。
COPY backend/ ./backend/

# 发布身份由流水线传入：APP_VERSION 写进程序集版本（GET /api/v1/service-info 返回的 version），
# APP_REVISION 是源码提交，写进镜像标签 org.opencontainers.image.revision。不传时版本为 1.0.0、提交为空
ARG APP_VERSION=1.0.0
ARG APP_REVISION=

# Build（还原会自动拉取 Leistd.* 框架包）
WORKDIR "/src/backend/src/CompanyName.ProjectName.Api"
RUN dotnet publish "CompanyName.ProjectName.Api.csproj" -c Release -o /app/api /p:UseAppHost=false /p:Version=$APP_VERSION

WORKDIR "/src/backend/src/CompanyName.ProjectName.DbMigrator"
RUN dotnet publish "CompanyName.ProjectName.DbMigrator.csproj" -c Release -o /app/migrator /p:UseAppHost=false /p:Version=$APP_VERSION

# -----------------------------------
# Migrator Runtime Image
# -----------------------------------
# DbMigrator 引用 Microsoft.AspNetCore.App，运行镜像必须包含 ASP.NET Core 共享框架。
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS migrator
ARG APP_VERSION=1.0.0
ARG APP_REVISION=
LABEL org.opencontainers.image.version=$APP_VERSION org.opencontainers.image.revision=$APP_REVISION
WORKDIR /app
COPY --from=backend-build /app/migrator .
# 以镜像自带的非 root 用户（UID 1654）运行；程序目录只读即可
USER $APP_UID
ENTRYPOINT ["dotnet", "CompanyName.ProjectName.DbMigrator.dll"]

# -----------------------------------
# API Runtime Image
# -----------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
ARG APP_VERSION=1.0.0
ARG APP_REVISION=
LABEL org.opencontainers.image.version=$APP_VERSION org.opencontainers.image.revision=$APP_REVISION
WORKDIR /app

EXPOSE 8080

# 拷贝后端构建结果
COPY --from=backend-build /app/api .
#if (SpaFrontend)

# 拷贝前端构建结果到后端的 wwwroot 目录，让 ASP.NET Core 直接托管静态 SPA 文件
# Angular >= 17 生产构建会输出到 dist/<project>/browser 目录
COPY --from=frontend-build /app/dist/companyname-projectname-web/browser ./wwwroot
#endif

#if (SpaFrontend)
# 以镜像自带的非 root 用户（UID 1654）运行：程序与静态文件只读；挂进来的证书要对它可读，
# 用文件目录保存 Data Protection 密钥时该目录要对它可写（见 docs/deploy/README.md）
#else
# 以镜像自带的非 root 用户（UID 1654）运行：程序目录只读
#endif
USER $APP_UID
ENTRYPOINT ["dotnet", "CompanyName.ProjectName.Api.dll"]
