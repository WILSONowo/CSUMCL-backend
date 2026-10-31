# CSUMCL Backend

CSUMCL 启动器的 ASP.NET Core 资源站与管理后台，目标框架为 .NET 9。此仓库仅包含后端源码、测试及通用部署模板；客户端源码位于 https://github.com/yuhui258036/CMU-MCL。

## 功能

- 公开 HTTPS 静态资源站、仅绑定回环地址的管理后台。
- 管理员密码 PBKDF2 验证、会话与 CSRF 检查。
- 整合包上传进度界面、revision 管理、签名 manifest。
- 从 .mrpack 生成增量文件索引与 overrides 哈希。
- 上传时提取 Minecraft / 加载器元数据，启动时回填已有记录。
- Range 下载、整合包长期缓存及元数据缓存控制。

客户端自动更新的公开 ZIP 和签名清单由静态资源目录提供；本仓库不包含生产更新包或生产签名材料。

## 构建与测试

安装能够构建 net9.0 的 .NET SDK（本次使用 .NET 10 SDK 构建、运行）。

```sh
dotnet build PclCmu.Admin.csproj -c Release
dotnet run --project tests/PclCmu.Admin.Tests -c Release
```

测试在临时目录创建合成整合包及临时签名密钥，完成后清理；不需要真实服务器数据、真实账号或生产私钥。

## 配置

将 appsettings.example.json 复制为本机 appsettings.json，并填写实际公开域名、证书路径和外部数据路径。该本机文件已被 Git 忽略。示例中的 example.com 不是可直接使用的部署地址。

管理员密码哈希通过 PCL_CMU_ADMIN_PASSWORD_HASH 环境变量传入，服务模板从服务器外部的 /opt/pcl-cmu/admin.env 加载它。签名私钥和 TLS 私钥由运维人员独立配置到仓库之外；不要将密钥内容写进 appsettings 或源码。

公开监听器默认示例为 HTTPS 443；管理监听器必须保持 127.0.0.1:9443，仅通过 SSH 隧道访问。线上整合包和实例状态存放在外部 wwwroot / data 目录，升级前单独备份，并保留现有实例和 revision。

## 部署模板

```sh
dotnet publish PclCmu.Admin.csproj -c Release -r linux-x64 --self-contained true -o publish
```

部署前准备有效 appsettings.json、管理员环境配置、签名密钥和证书。deploy/pcl-cmu.service 示例使用 /opt/dotnet/dotnet，按实际安装方式调整。deploy/install.sh 是需审核后使用的安装模板，会复制应用并重启服务；其中自动生成的 example.com 自签证书仅供开发示例，生产请预先配置受信任证书。

不要用空数据目录覆盖现有服务器数据。仓库不包含生产 data、wwwroot、证书、私钥、密码哈希、日志、构建产物或实际服务器地址。上传此源码仓库不会执行线上部署。

## 提交范围与敏感信息

.gitignore 采用源码白名单并额外排除密钥、环境文件和运行目录；项目发布规则也排除证书、密钥及凭据文件。只提交审核过的源码和示例。新增配置项时请同步检查是否含密码、令牌、连接字符串或私钥；不要使用 git add -f 绕过排除规则。

参见 SECURITY.md。