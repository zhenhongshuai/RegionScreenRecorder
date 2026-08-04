@echo off

:: 设置Git仓库的路径
::set REPO_PATH=C:\path\to\your\git\repo

:: 进入Git仓库目录
::cd /d %REPO_PATH%

:: 提示用户输入提交信息前缀
set /p commit_prefix="请输入提交信息前缀（例如'Commit on'）: "  

:: 配置Git用户信息（如果尚未配置）
::git config --global user.email "your.email@example.com"
::git config --global user.name "Your Name"

:: 获取当前日期和时间
set "current_date=%date%"
set "current_time=%time%"

:: 去除时间字符串中的空格和冒号，以便用作提交信息
set "clean_time=%current_time::=%"
set "clean_time=%clean_time: =%"

:: 构造提交信息
set "commit_message=%commit_prefix% %current_date% at %clean_time%"

:: 添加所有更改到暂存区
git add .

:: 提交更改
git commit -m "%commit_message%"

:: 推送到远程仓库
git push https://gitee.com/zhenhongshuai/region-screen-recorder.git "master"

:: 退出批处理
exit