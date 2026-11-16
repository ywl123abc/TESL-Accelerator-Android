using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Net;
using Android.OS;
using Android.Views;
using Android.Widget;
using TESL.Accelerator.AndroidHost.Adapters;
using TESL.Accelerator.AndroidHost.Interop;
using TESL.Accelerator.AndroidHost.Lifecycle;
using TESL.Accelerator.AndroidHost.Logging;
using TESL.Accelerator.AndroidHost.Services;
using TESL.Accelerator.Core.Models;
using TESL.Accelerator.Core.Services;

namespace TESL.Accelerator.AndroidHost.Platforms.Android
{
    [Activity(
        Name = "com.tesl.accelerator.MainActivity",
        Label = "TESL 加速器",
        Icon = "@mipmap/ic_launcher",
        RoundIcon = "@mipmap/ic_launcher_round",
        MainLauncher = true,
        Exported = true,
        Theme = "@android:style/Theme.DeviceDefault.NoActionBar"
    )]
    public class MainActivity : Activity, View.IOnApplyWindowInsetsListener
    {
        private const int RequestVpnPermission = 1002;

        private LinearLayout? _mainLayout;

        // UI 核心控件
        private TextView? _tvStatusBadge;
        private TextView? _tvStatusTitle;
        private TextView? _tvStatusDesc;
        private TextView? _tvTunInfo;
        private TextView? _tvTrafficInfo;

        private Button? _btnToggle;
        private Button? _btnRescanAll;
        private Button? _btnUpdate;

        // 443 卡片控件
        private TextView? _tv443State;
        private TextView? _tv443Ip;
        private TextView? _tv443Latency;
        private TextView? _tv443Candidates;
        private TextView? _tv443Connections;
        private TextView? _tv443Error;
        private Button? _btn443Rescan;
        private Button? _btn443Toggle;

        // 8443 卡片控件
        private TextView? _tv8443State;
        private TextView? _tv8443Ip;
        private TextView? _tv8443Latency;
        private TextView? _tv8443Candidates;
        private TextView? _tv8443Connections;
        private TextView? _tv8443Error;
        private Button? _btn8443Rescan;
        private Button? _btn8443Toggle;

        // 日志瀑布流控件
        private TextView? _tvLogCount;
        private LinearLayout? _layoutLogContainer;
        private ScrollView? _scrollLogView;
        private Button? _btnClearLog;

        private readonly Handler _handler = new(Looper.MainLooper);
        private bool _isDestroyed = false;
        private bool _isActionInProgress = false;

        private class ChannelTrackState
        {
            public string LastIp = string.Empty;
            public int LastLatency = 0;
            public DateTime LastUpdateTime = DateTime.MinValue;
            public bool IsFullScanCompleted = false;
        }

        private readonly ChannelTrackState _track443 = new();
        private readonly ChannelTrackState _track8443 = new();

        // 基础设计边距 (基于屏幕 Density 动态换算物理像素，禁止在已有 Padding 上累加)
        private const int BasePaddingHorizontalDp = 16;
        private const int BasePaddingTopExtraDp = 12;
        private const int BasePaddingBottomExtraDp = 32;

        private int DpToPx(int dp)
        {
            float density = Resources?.DisplayMetrics?.Density ?? 1.0f;
            return (int)Math.Ceiling(dp * density);
        }

        private int GetStatusBarHeightFallback()
        {
            int resourceId = Resources?.GetIdentifier("status_bar_height", "dimen", "android") ?? 0;
            if (resourceId > 0)
            {
                return Resources?.GetDimensionPixelSize(resourceId) ?? DpToPx(36);
            }
            return DpToPx(36);
        }

        private int GetNavigationBarHeightFallback()
        {
            int resourceId = Resources?.GetIdentifier("navigation_bar_height", "dimen", "android") ?? 0;
            if (resourceId > 0)
            {
                return Resources?.GetDimensionPixelSize(resourceId) ?? DpToPx(48);
            }
            return DpToPx(48);
        }

        public WindowInsets OnApplyWindowInsets(View? v, WindowInsets insets)
        {
            if (insets != null && _mainLayout != null)
            {
                int topInset = 0;
                int bottomInset = 0;
                int leftInset = 0;
                int rightInset = 0;

#pragma warning disable CA1416, CA1422
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    // API 30+ (Android 11 ~ 15): 统一读取系统栏与显示挖孔 (纵向及横向 DisplayCutout)
                    var insetsType = WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout();
                    var systemBars = insets.GetInsets(insetsType);
                    topInset = systemBars.Top;
                    bottomInset = systemBars.Bottom;
                    leftInset = systemBars.Left;
                    rightInset = systemBars.Right;
                }
                else if (Build.VERSION.SdkInt >= BuildVersionCodes.P && insets.DisplayCutout != null)
                {
                    // API 28 ~ 29 (Android 9 ~ 10): 结合 SystemWindowInsets 与 DisplayCutout 安全边距
                    var cutout = insets.DisplayCutout;
                    topInset = Math.Max(insets.SystemWindowInsetTop, cutout.SafeInsetTop);
                    bottomInset = Math.Max(insets.SystemWindowInsetBottom, cutout.SafeInsetBottom);
                    leftInset = Math.Max(insets.SystemWindowInsetLeft, cutout.SafeInsetLeft);
                    rightInset = Math.Max(insets.SystemWindowInsetRight, cutout.SafeInsetRight);
                }
                else
                {
                    // API 26 ~ 27 (Android 8.0 ~ 8.1): 传统系统窗口边距，旧设备无刘海挖孔
                    topInset = insets.SystemWindowInsetTop;
                    bottomInset = insets.SystemWindowInsetBottom;
                    leftInset = insets.SystemWindowInsetLeft;
                    rightInset = insets.SystemWindowInsetRight;
                }
#pragma warning restore CA1416, CA1422

                // 幂等纯函数计算：以基础设计边距为基准 + 当前 Insets 动态赋值，绝不叠加已有 Padding
                int densityPaddingH = DpToPx(BasePaddingHorizontalDp);
                int padLeft = Math.Max(densityPaddingH, leftInset);
                int padRight = Math.Max(densityPaddingH, rightInset);
                int padTop = (topInset > 0 ? topInset : GetStatusBarHeightFallback()) + DpToPx(BasePaddingTopExtraDp);
                int padBottom = (bottomInset > 0 ? bottomInset : GetNavigationBarHeightFallback()) + DpToPx(BasePaddingBottomExtraDp);

                _mainLayout.SetPadding(padLeft, padTop, padRight, padBottom);
            }
            return insets!;
        }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // 1. 根 Window DecorView 铺满极黑底色 #121212，承载 Android 15 Edge-to-Edge 系统栏透明后方的实际底色
            Window?.DecorView?.SetBackgroundColor(Color.ParseColor("#121212"));

            // 2. 系统栏图标明暗控制：暗色背景下强制使用明亮（白色）系统栏图标，保证电量、时间、信号及手势条清晰可见
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                var insetsController = Window?.InsetsController;
                if (insetsController != null)
                {
                    // 传入 0 表示清除 LightStatusBars/LightNavigationBars 标记，采用高对比度白色系统图标
                    insetsController.SetSystemBarsAppearance(
                        0,
                        (int)(WindowInsetsControllerAppearance.LightStatusBars | WindowInsetsControllerAppearance.LightNavigationBars)
                    );
                }
            }
            else if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
            {
#pragma warning disable CA1422
                if (Window?.DecorView != null)
                {
                    var flags = Window.DecorView.SystemUiFlags;
                    flags &= ~SystemUiFlags.LightStatusBar;
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    {
                        flags &= ~SystemUiFlags.LightNavigationBar;
                    }
                    Window.DecorView.SystemUiFlags = flags;
                }
#pragma warning restore CA1422
            }

            // 3. 针对 API < 35 设备设置系统栏颜色（API 35+ / TargetSDK 35+ 由系统全透明接管，依靠 DecorView 绘制）
            if (Build.VERSION.SdkInt < (BuildVersionCodes)35 && Window != null)
            {
#pragma warning disable CA1422
                Window.SetStatusBarColor(Color.ParseColor("#121212"));
                Window.SetNavigationBarColor(Color.ParseColor("#121212"));
#pragma warning restore CA1422
            }

            // 根滚动视图 (适配各种手机屏高与全屏边缘避让)
            var rootScroll = new ScrollView(this)
            {
                LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent)
            };
            rootScroll.SetClipToPadding(false);
            rootScroll.SetBackgroundColor(Color.ParseColor("#121212")); // 暗色极黑背景

            _mainLayout = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical,
                LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            };

            // 初始安全边距 (基于屏幕 Density 计算 fallback，避开顶部状态栏和底部导航栏)
            int initialTop = GetStatusBarHeightFallback() + DpToPx(BasePaddingTopExtraDp);
            int initialBottom = GetNavigationBarHeightFallback() + DpToPx(BasePaddingBottomExtraDp);
            int initialH = DpToPx(BasePaddingHorizontalDp);
            _mainLayout.SetPadding(initialH, initialTop, initialH, initialBottom);

            // 注册 WindowInsets 动态监听器，精确避让状态栏、刘海挖孔与底部手势/3按键导航栏 (Android 8.0 ~ 15)
            rootScroll.SetOnApplyWindowInsetsListener(this);

            // 1. 标题栏与核心状态看板
            BuildHeaderSection(_mainLayout);

            // 2. 全局控制按钮栏
            BuildControlBar(_mainLayout);

            // 3. 双通道状态卡片 (443 与 8443 并列/堆叠展示)
            BuildChannelCards(_mainLayout);

            // 4. 实时日志瀑布流
            BuildLogWaterfallSection(_mainLayout);

            rootScroll.AddView(_mainLayout);
            SetContentView(rootScroll);

            // 订阅全局日志与服务生命周期通知
            AppLogBuffer.OnLogAppended += OnLogReceivedInternal;
            TeslVpnService.OnServiceChanged += OnServiceStateChangedInternal;
            AndroidUpdateService.OnStateChanged += OnUpdateServiceStateChanged;

            // 启动 1000ms 轮询刷新定时器 (保证展示真实瞬时状态)
            StartStatusPolling();

            // 启动后后台异步静默检查更新 (6小时缓存间隔，不阻塞启动)
            AndroidUpdateService.CheckUpdateBackground(this, isManual: false);

            HandleIntentAction(Intent);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            _isDestroyed = true;
            AppLogBuffer.OnLogAppended -= OnLogReceivedInternal;
            TeslVpnService.OnServiceChanged -= OnServiceStateChangedInternal;
            AndroidUpdateService.OnStateChanged -= OnUpdateServiceStateChanged;
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            HandleIntentAction(intent);
        }

        private void HandleIntentAction(Intent? intent)
        {
            if (intent == null) return;
            string? action = intent.GetStringExtra("action");
            if (string.Equals(action, "start", StringComparison.OrdinalIgnoreCase))
            {
                RequestStartVpn();
            }
            else if (string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase))
            {
                RequestStopVpn();
            }
        }

        #region UI 布局构建

        private void BuildHeaderSection(LinearLayout parent)
        {
            var headerCard = CreateCardView();

            var titleLayout = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            titleLayout.SetGravity(GravityFlags.CenterVertical);

            var title = new TextView(this)
            {
                Text = "TESL 加速器",
                TextSize = 20f,
                Typeface = Typeface.DefaultBold
            };
            title.SetTextColor(Color.White);
            titleLayout.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.0f));

            _tvStatusBadge = new TextView(this)
            {
                Text = " 未连接 ",
                TextSize = 12f,
                Typeface = Typeface.DefaultBold
            };
            _tvStatusBadge.SetPadding(16, 6, 16, 6);
            _tvStatusBadge.Background = CreatePillDrawable(Color.ParseColor("#424242"));
            _tvStatusBadge.SetTextColor(Color.White);
            titleLayout.AddView(_tvStatusBadge);

            headerCard.AddView(titleLayout);

            _tvStatusTitle = new TextView(this)
            {
                Text = "就绪 · 等待启动",
                TextSize = 15f
            };
            _tvStatusTitle.SetTextColor(Color.ParseColor("#00E5FF")); // 高亮青色
            _tvStatusTitle.SetPadding(0, 16, 0, 4);
            headerCard.AddView(_tvStatusTitle);

            _tvStatusDesc = new TextView(this)
            {
                Text = "双通道新加坡 Cloudflare Anycast · sing-box v1.11.10 内核",
                TextSize = 12f
            };
            _tvStatusDesc.SetTextColor(Color.ParseColor("#9E9E9E"));
            headerCard.AddView(_tvStatusDesc);

            // 分割线
            var divider = new View(this) { LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1) };
            divider.SetBackgroundColor(Color.ParseColor("#2C2C2C"));
            divider.SetPadding(0, 16, 0, 16);
            headerCard.AddView(divider);

            _tvTunInfo = new TextView(this)
            {
                Text = "TUN 路由: 未激活 · sing-box: 已停止",
                TextSize = 12f
            };
            _tvTunInfo.SetTextColor(Color.ParseColor("#B0BEC5"));
            headerCard.AddView(_tvTunInfo);

            _tvTrafficInfo = new TextView(this)
            {
                Text = "实时流量: ↑ 0.0 KB   ↓ 0.0 KB",
                TextSize = 12f
            };
            _tvTrafficInfo.SetTextColor(Color.ParseColor("#81C784"));
            _tvTrafficInfo.SetPadding(0, 6, 0, 0);
            headerCard.AddView(_tvTrafficInfo);

            parent.AddView(headerCard);
        }

        private void BuildControlBar(LinearLayout parent)
        {
            var controlContainer = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical,
                LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            };
            controlContainer.SetPadding(0, 0, 0, DpToPx(16));

            var btnRow = new LinearLayout(this)
            {
                Orientation = Orientation.Horizontal,
                LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            };

            _btnToggle = new Button(this)
            {
                Text = "启动加速",
                TextSize = 16f,
                Typeface = Typeface.DefaultBold
            };
            _btnToggle.SetTextColor(Color.White);
            _btnToggle.Background = CreateButtonDrawable(Color.ParseColor("#00C853"));
            _btnToggle.Click += (s, e) => OnToggleClicked();

            var toggleLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 2.0f);
            toggleLp.SetMargins(0, 0, DpToPx(12), 0);
            btnRow.AddView(_btnToggle, toggleLp);

            _btnRescanAll = new Button(this)
            {
                Text = "全通道重测速",
                TextSize = 14f
            };
            _btnRescanAll.SetTextColor(Color.ParseColor("#E0E0E0"));
            _btnRescanAll.Background = CreateButtonDrawable(Color.ParseColor("#37474F"));
            _btnRescanAll.Click += (s, e) =>
            {
                _track443.IsFullScanCompleted = false;
                _track8443.IsFullScanCompleted = false;
                OnRescanAllClicked();
            };

            var rescanLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.2f);
            btnRow.AddView(_btnRescanAll, rescanLp);

            controlContainer.AddView(btnRow);

            // 独立更新检查按钮 (满足无新版本、发现新版本、检查中、下载中、检查失败 5 种状态)
            _btnUpdate = new Button(this)
            {
                Text = "检查更新",
                TextSize = 13f,
                Typeface = Typeface.DefaultBold
            };
            _btnUpdate.SetTextColor(Color.ParseColor("#B0BEC5"));
            _btnUpdate.Background = CreateButtonDrawable(Color.ParseColor("#263238"));
            _btnUpdate.Click += (s, e) => OnUpdateBtnClicked();

            var updateLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            updateLp.SetMargins(0, DpToPx(10), 0, 0);
            controlContainer.AddView(_btnUpdate, updateLp);

            parent.AddView(controlContainer);
        }

        private void OnUpdateServiceStateChanged(UpdateCheckState state, ReleaseInfo? release)
        {
            RunOnUiThread(() =>
            {
                if (_isDestroyed || _btnUpdate == null) return;

                switch (state)
                {
                    case UpdateCheckState.Checking:
                        _btnUpdate.Text = "正在检查更新...";
                        _btnUpdate.Enabled = false;
                        _btnUpdate.SetTextColor(Color.ParseColor("#90CAF9"));
                        _btnUpdate.Background = CreateButtonDrawable(Color.ParseColor("#263238"));
                        break;
                    case UpdateCheckState.Downloading:
                        _btnUpdate.Text = "正在下载更新...";
                        _btnUpdate.Enabled = false;
                        _btnUpdate.SetTextColor(Color.ParseColor("#FFD54F"));
                        _btnUpdate.Background = CreateButtonDrawable(Color.ParseColor("#37474F"));
                        break;
                    case UpdateCheckState.HasUpdate:
                        _btnUpdate.Text = "有新版本，现在更新";
                        _btnUpdate.Enabled = true;
                        _btnUpdate.SetTextColor(Color.White);
                        _btnUpdate.Background = CreateButtonDrawable(Color.ParseColor("#FF6D00")); // 高亮橙色
                        break;
                    case UpdateCheckState.UpToDate:
                        _btnUpdate.Text = "检查更新 (当前已是最新版)";
                        _btnUpdate.Enabled = true;
                        _btnUpdate.SetTextColor(Color.ParseColor("#81C784"));
                        _btnUpdate.Background = CreateButtonDrawable(Color.ParseColor("#1B2A1E"));
                        break;
                    case UpdateCheckState.Error:
                        _btnUpdate.Text = "检查更新失败，点击重试";
                        _btnUpdate.Enabled = true;
                        _btnUpdate.SetTextColor(Color.ParseColor("#EF9A9A"));
                        _btnUpdate.Background = CreateButtonDrawable(Color.ParseColor("#3E2723"));
                        break;
                    default:
                        _btnUpdate.Text = "检查更新";
                        _btnUpdate.Enabled = true;
                        _btnUpdate.SetTextColor(Color.ParseColor("#B0BEC5"));
                        _btnUpdate.Background = CreateButtonDrawable(Color.ParseColor("#263238"));
                        break;
                }
            });
        }

        private void OnUpdateBtnClicked()
        {
            if (AndroidUpdateService.CurrentState == UpdateCheckState.HasUpdate && AndroidUpdateService.LatestRelease != null)
            {
                ShowUpdateLogDialog(AndroidUpdateService.LatestRelease);
            }
            else if (AndroidUpdateService.CurrentState != UpdateCheckState.Checking && AndroidUpdateService.CurrentState != UpdateCheckState.Downloading)
            {
                AndroidUpdateService.CheckUpdateBackground(this, isManual: true);
            }
        }

        private void ShowUpdateLogDialog(ReleaseInfo release)
        {
            var dialog = new AlertDialog.Builder(this)
                .SetTitle($"发现新版本 {release.TagName}")
                .SetMessage(
                    $"当前版本: v{AndroidUpdateService.GetCurrentVersionName(this)} (Code {AndroidUpdateService.GetCurrentVersionCode(this)})\n" +
                    $"最新版本: {release.TagName} (Code {release.VersionCode})\n" +
                    $"发布时间: {release.PublishedAt}\n" +
                    $"安装包大小: {(release.ApkSize > 0 ? (release.ApkSize / (1024.0 * 1024.0)).ToString("F1") + " MB" : "未知")}\n" +
                    $"元数据来源: {release.Source}\n\n" +
                    $"更新日志:\n{(string.IsNullOrWhiteSpace(release.Changelog) ? "优化系统性能与连接稳定性。" : release.Changelog)}"
                )
                .SetPositiveButton("立即更新", async (s, e) =>
                {
                    await StartDownloadAndInstall(release);
                })
                .SetNegativeButton("暂不更新", (s, e) => { })
                .Create();

            dialog?.Show();
        }

        private async Task StartDownloadAndInstall(ReleaseInfo release)
        {
#pragma warning disable CA1422
            var progressDialog = new ProgressDialog(this);
            progressDialog.SetTitle("正在下载更新");
            progressDialog.SetMessage("准备下载安装包...");
            progressDialog.SetProgressStyle(ProgressDialogStyle.Horizontal);
            progressDialog.Max = 100;
            progressDialog.Progress = 0;
            progressDialog.SetCancelable(false);
            progressDialog.Show();

            bool ok = await AndroidUpdateService.DownloadAndInstallApkAsync(this, release, (progress) =>
            {
                RunOnUiThread(() =>
                {
                    progressDialog.Progress = progress;
                    progressDialog.SetMessage($"正在下载更新... {progress}%");
                });
            });

            progressDialog.Dismiss();
#pragma warning restore CA1422
        }

        private void BuildChannelCards(LinearLayout parent)
        {
            // --- 443 通道卡片 ---
            var card443 = CreateCardView();
            card443.AddView(CreateChannelHeader("443 业务通道 (游戏主要业务)", out _tv443State));

            _tv443Ip = CreateKeyValueRow(card443, "当前优选 IP:", "探测中...");
            _tv443Latency = CreateKeyValueRow(card443, "实测延迟:", "-- ms");
            _tv443Candidates = CreateKeyValueRow(card443, "候选节点数:", "107 个");
            _tv443Connections = CreateKeyValueRow(card443, "活跃连接数:", "0 个连接 (本地 127.0.0.1:10443)");
            _tv443Error = CreateKeyValueRow(card443, "通道状态信息:", "就绪");

            var btnRow443 = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            btnRow443.SetPadding(0, 16, 0, 0);

            _btn443Rescan = CreateCardButton("单独测速");
            _btn443Rescan.Click += (s, e) =>
            {
                _track443.IsFullScanCompleted = false;
                TeslVpnService.Instance?.EngineHost?.TriggerRescan443();
                Toast.MakeText(this, "已下发 443 独立重新测速指令", ToastLength.Short)?.Show();
            };
            btnRow443.AddView(_btn443Rescan, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            _btn443Toggle = CreateCardButton("独立启停");
            _btn443Toggle.Click += (s, e) =>
            {
                var host = TeslVpnService.Instance?.EngineHost;
                if (host != null)
                {
                    var st = host.GetStatus443();
                    if (st.HasValue && st.Value.State != CfnatState.Stopped)
                    {
                        host.StopChannel443();
                        Toast.MakeText(this, "已单独停止 443 实例", ToastLength.Short)?.Show();
                    }
                    else
                    {
                        host.StartChannel443();
                        Toast.MakeText(this, "已单独恢复 443 实例", ToastLength.Short)?.Show();
                    }
                }
            };
            var lpBtn443 = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            lpBtn443.SetMargins(16, 0, 0, 0);
            btnRow443.AddView(_btn443Toggle, lpBtn443);

            card443.AddView(btnRow443);
            parent.AddView(card443);

            // --- 8443 通道卡片 ---
            var card8443 = CreateCardView();
            card8443.AddView(CreateChannelHeader("8443 对战信令通道 (PubSub)", out _tv8443State));

            _tv8443Ip = CreateKeyValueRow(card8443, "当前优选 IP:", "探测中...");
            _tv8443Latency = CreateKeyValueRow(card8443, "实测延迟:", "-- ms");
            _tv8443Candidates = CreateKeyValueRow(card8443, "候选节点数:", "105 个");
            _tv8443Connections = CreateKeyValueRow(card8443, "活跃连接数:", "0 个连接 (本地 127.0.0.1:18443)");
            _tv8443Error = CreateKeyValueRow(card8443, "通道状态信息:", "就绪");

            var btnRow8443 = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            btnRow8443.SetPadding(0, 16, 0, 0);

            _btn8443Rescan = CreateCardButton("单独测速");
            _btn8443Rescan.Click += (s, e) =>
            {
                _track8443.IsFullScanCompleted = false;
                TeslVpnService.Instance?.EngineHost?.TriggerRescan8443();
                Toast.MakeText(this, "已下发 8443 独立重新测速指令", ToastLength.Short)?.Show();
            };
            btnRow8443.AddView(_btn8443Rescan, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            _btn8443Toggle = CreateCardButton("独立启停");
            _btn8443Toggle.Click += (s, e) =>
            {
                var host = TeslVpnService.Instance?.EngineHost;
                if (host != null)
                {
                    var st = host.GetStatus8443();
                    if (st.HasValue && st.Value.State != CfnatState.Stopped)
                    {
                        host.StopChannel8443();
                        Toast.MakeText(this, "已单独停止 8443 实例", ToastLength.Short)?.Show();
                    }
                    else
                    {
                        host.StartChannel8443();
                        Toast.MakeText(this, "已单独恢复 8443 实例", ToastLength.Short)?.Show();
                    }
                }
            };
            var lpBtn8443 = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            lpBtn8443.SetMargins(16, 0, 0, 0);
            btnRow8443.AddView(_btn8443Toggle, lpBtn8443);

            card8443.AddView(btnRow8443);
            parent.AddView(card8443);
        }

        private void BuildLogWaterfallSection(LinearLayout parent)
        {
            var logCard = CreateCardView();

            var topRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            topRow.SetGravity(GravityFlags.CenterVertical);

            var logTitle = new TextView(this)
            {
                Text = "实时日志瀑布流",
                TextSize = 14f,
                Typeface = Typeface.DefaultBold
            };
            logTitle.SetTextColor(Color.White);
            topRow.AddView(logTitle, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            _tvLogCount = new TextView(this)
            {
                Text = "0 条",
                TextSize = 12f
            };
            _tvLogCount.SetTextColor(Color.ParseColor("#757575"));
            topRow.AddView(_tvLogCount);

            _btnClearLog = new Button(this)
            {
                Text = "清空",
                TextSize = 11f
            };
            _btnClearLog.SetPadding(20, 6, 20, 6);
            _btnClearLog.SetTextColor(Color.ParseColor("#B0BEC5"));
            _btnClearLog.Background = CreateButtonDrawable(Color.ParseColor("#263238"));
            _btnClearLog.Click += (s, e) =>
            {
                AppLogBuffer.Clear();
                _layoutLogContainer?.RemoveAllViews();
                UpdateLogCount(0);
            };
            var clearLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            clearLp.SetMargins(16, 0, 0, 0);
            topRow.AddView(_btnClearLog, clearLp);

            logCard.AddView(topRow);

            // 滚动日志显示区 (最大高度 260dp)
            _scrollLogView = new ScrollView(this)
            {
                LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(260 * Resources!.DisplayMetrics!.Density))
            };
            _scrollLogView.SetBackgroundColor(Color.ParseColor("#0A0A0A")); // 纯黑终端质感
            _scrollLogView.SetPadding(16, 16, 16, 16);

            _layoutLogContainer = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical,
                LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            };
            _scrollLogView.AddView(_layoutLogContainer);

            var scrollLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(260 * Resources.DisplayMetrics.Density));
            scrollLp.SetMargins(0, 16, 0, 0);
            logCard.AddView(_scrollLogView, scrollLp);

            parent.AddView(logCard);

            // 载入已有历史日志 (经过 UI 降噪过滤器)
            var logs = AppLogBuffer.Snapshot();
            int displayedCount = 0;
            foreach (var l in logs)
            {
                if (ShouldDisplayInUiWaterfall(l))
                {
                    AddLogLineToContainer(l);
                    displayedCount++;
                }
            }
            UpdateLogCount(displayedCount);
        }

        #endregion

        #region UI 辅助创建方法

        private LinearLayout CreateCardView()
        {
            var card = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical
            };
            int p16 = DpToPx(16);
            card.SetPadding(p16, p16, p16, p16);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.SetMargins(0, 0, 0, DpToPx(16));
            card.LayoutParameters = lp;
            card.Background = CreateCardDrawable();
            return card;
        }

        private Drawable CreateCardDrawable()
        {
            var drawable = new GradientDrawable();
            drawable.SetColor(Color.ParseColor("#1E1E1E"));
            drawable.SetCornerRadius(DpToPx(12));
            drawable.SetStroke(DpToPx(1), Color.ParseColor("#2E2E2E"));
            return drawable;
        }

        private Drawable CreatePillDrawable(Color color)
        {
            var drawable = new GradientDrawable();
            drawable.SetColor(color);
            drawable.SetCornerRadius(DpToPx(20));
            return drawable;
        }

        private Drawable CreateButtonDrawable(Color color)
        {
            var drawable = new GradientDrawable();
            drawable.SetColor(color);
            drawable.SetCornerRadius(DpToPx(10));
            return drawable;
        }

        private View CreateChannelHeader(string titleText, out TextView stateView)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(0, 0, 0, 12);

            var title = new TextView(this)
            {
                Text = titleText,
                TextSize = 14f,
                Typeface = Typeface.DefaultBold
            };
            title.SetTextColor(Color.White);
            row.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            stateView = new TextView(this)
            {
                Text = " STOPPED ",
                TextSize = 11f,
                Typeface = Typeface.DefaultBold
            };
            stateView.SetPadding(12, 4, 12, 4);
            stateView.Background = CreatePillDrawable(Color.ParseColor("#424242"));
            stateView.SetTextColor(Color.White);
            row.AddView(stateView);

            return row;
        }

        private TextView CreateKeyValueRow(LinearLayout parent, string key, string initialValue)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetPadding(0, 4, 0, 4);

            var tvKey = new TextView(this)
            {
                Text = key,
                TextSize = 12f
            };
            tvKey.SetTextColor(Color.ParseColor("#9E9E9E"));
            row.AddView(tvKey, new LinearLayout.LayoutParams((int)(110 * Resources!.DisplayMetrics!.Density), ViewGroup.LayoutParams.WrapContent));

            var tvVal = new TextView(this)
            {
                Text = initialValue,
                TextSize = 12f
            };
            tvVal.SetTextColor(Color.ParseColor("#E0E0E0"));
            row.AddView(tvVal, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            parent.AddView(row);
            return tvVal;
        }

        private Button CreateCardButton(string text)
        {
            var btn = new Button(this)
            {
                Text = text,
                TextSize = 12f
            };
            btn.SetPadding(16, 8, 16, 8);
            btn.SetTextColor(Color.ParseColor("#ECEFF1"));
            btn.Background = CreateButtonDrawable(Color.ParseColor("#2C3840"));
            return btn;
        }

        #endregion

        #region 状态轮询与事件响应

        private void StartStatusPolling()
        {
            _handler.PostDelayed(new Action(async () =>
            {
                if (_isDestroyed) return;
                UpdateUiFromLiveState();
                StartStatusPolling();
            }), 1000);
        }

        private void OnServiceStateChangedInternal()
        {
            RunOnUiThread(UpdateUiFromLiveState);
        }

        private void OnLogReceivedInternal(LogItem item)
        {
            if (item == null) return;

            // 监听全量优选扫描完成事件与缓存保存事件
            if (item.Message.Contains("全量候选节点优选扫描完成") || item.Message.Contains("优选缓存已原子保存至"))
            {
                if (item.Source.Contains("443")) _track443.IsFullScanCompleted = true;
                if (item.Source.Contains("8443")) _track8443.IsFullScanCompleted = true;
            }

            if (!ShouldDisplayInUiWaterfall(item)) return;

            RunOnUiThread(() =>
            {
                AddLogLineToContainer(item);
                UpdateLogCount(_layoutLogContainer?.ChildCount ?? 0);
            });
        }

        private static bool ShouldDisplayInUiWaterfall(LogItem item)
        {
            if (item == null) return false;
            string lvl = item.Level?.ToUpperInvariant() ?? "INFO";

            // 1. Error 与 Warn：始终显示，保障故障可见性与可排查性
            if (lvl == "ERROR" || lvl == "WARN") return true;

            // 2. Debug 与 Trace：绝不刷入普通 UI 瀑布流，完整保留在内部 AppLogBuffer 和诊断日志
            if (lvl == "DEBUG" || lvl == "TRACE") return false;

            // 3. Info 级别：精细化筛选关键业务事件，屏蔽常规单节点探针握手详情
            string msg = item.Message ?? string.Empty;

            // 过滤常规单节点探测握手详情 (例如 "候选节点健康检查成功: x.x.x.x")
            if (msg.Contains("候选节点健康检查") || msg.Contains("ClientHello") || msg.Contains("ServerHello"))
            {
                return false;
            }

            // 允许进入普通 UI 瀑布流的重要事件：
            // 引擎启停、READY 就绪、节点迭代切换、网络出口变化、低频全量、故障自愈、优选缓存保存等
            return true;
        }

        private void AddLogLineToContainer(LogItem item)
        {
            if (_layoutLogContainer == null || !ShouldDisplayInUiWaterfall(item)) return;

            var tv = new TextView(this)
            {
                Text = item.ToString(),
                TextSize = 10f,
                Typeface = Typeface.Monospace
            };
            tv.SetPadding(0, 2, 0, 2);

            Color color = item.Level switch
            {
                "ERROR" => Color.ParseColor("#FF5252"),
                "WARN" => Color.ParseColor("#FFB300"),
                "DEBUG" => Color.ParseColor("#80D8FF"),
                _ => Color.ParseColor("#B0BEC5")
            };
            tv.SetTextColor(color);

            _layoutLogContainer.AddView(tv);

            // 控制视图最大子节点数量，保持有界 (500 条)
            if (_layoutLogContainer.ChildCount > 500)
            {
                _layoutLogContainer.RemoveViewAt(0);
            }

            // 滚动到底部
            _scrollLogView?.Post(() => _scrollLogView.FullScroll(FocusSearchDirection.Down));
        }

        private void UpdateLogCount(int count)
        {
            if (_tvLogCount != null)
            {
                _tvLogCount.Text = $"{count} 条";
            }
        }

        /// <summary>
        /// 从当前真实的 Coordinator、cfnat 原生桥接和 Linux 内核获取瞬时快照，绝不硬编码！
        /// </summary>
        private void UpdateUiFromLiveState()
        {
            var service = TeslVpnService.Instance;
            bool isServiceActive = service != null && service.IsRunning;
            var coord = service?.Coordinator;
            var host = service?.EngineHost;

            // 1. 全局加速状态与 Badge 刷新
            if (isServiceActive)
            {
                _tvStatusBadge!.Text = " 零中转加速中 ";
                _tvStatusBadge.Background = CreatePillDrawable(Color.ParseColor("#00C853"));
                _tvStatusTitle!.Text = coord?.CurrentStatus.Title ?? "零中转加速已生效";
                _tvStatusDesc!.Text = coord?.CurrentStatus.Subtitle ?? "TUN 路由接管中 · 新加坡 Anycast 双通道";
                _btnToggle!.Text = "停止加速";
                _btnToggle.Background = CreateButtonDrawable(Color.ParseColor("#D50000")); // 亮红
            }
            else
            {
                _tvStatusBadge!.Text = " 已断开 ";
                _tvStatusBadge.Background = CreatePillDrawable(Color.ParseColor("#424242"));
                _tvStatusTitle!.Text = "未启动加速";
                _tvStatusDesc!.Text = "点击下方按钮启动双通道 Anycast 零中转加速";
                _btnToggle!.Text = "启动加速";
                _btnToggle.Background = CreateButtonDrawable(Color.ParseColor("#00C853")); // 鲜绿
            }

            // 2. 虚拟网卡与流量
            if (isServiceActive)
            {
                _tvTunInfo!.Text = "TUN 路由: tun0 (172.19.0.1/30 MTU 1500) · sing-box: 运行中 (gVisor)";
                _tvTunInfo.SetTextColor(Color.ParseColor("#80D8FF"));

                long rx = TrafficStats.GetUidRxBytes(global::Android.OS.Process.MyUid());
                long tx = TrafficStats.GetUidTxBytes(global::Android.OS.Process.MyUid());
                _tvTrafficInfo!.Text = $"系统出入流量: ↑ {FormatBytes(tx)}   ↓ {FormatBytes(rx)}";
            }
            else
            {
                _tvTunInfo!.Text = "TUN 路由: 未激活 · sing-box: 已停止";
                _tvTunInfo.SetTextColor(Color.ParseColor("#B0BEC5"));
                _tvTrafficInfo!.Text = "系统出入流量: --";
            }

            // 3. 通道 443 真实状态
            var st443 = host?.GetStatus443();
            if (st443.HasValue)
            {
                var s = st443.Value;
                ApplyChannelStatusToViews(s, _tv443State!, _tv443Ip!, _tv443Latency!, _tv443Candidates!, _tv443Connections!, _tv443Error!, _btn443Toggle!, 10443, _track443);
            }
            else
            {
                ResetChannelViews(_tv443State!, _tv443Ip!, _tv443Latency!, _tv443Candidates!, _tv443Connections!, _tv443Error!, _btn443Toggle!, 10443, _track443);
            }

            // 4. 通道 8443 真实状态
            var st8443 = host?.GetStatus8443();
            if (st8443.HasValue)
            {
                var s = st8443.Value;
                ApplyChannelStatusToViews(s, _tv8443State!, _tv8443Ip!, _tv8443Latency!, _tv8443Candidates!, _tv8443Connections!, _tv8443Error!, _btn8443Toggle!, 18443, _track8443);
            }
            else
            {
                ResetChannelViews(_tv8443State!, _tv8443Ip!, _tv8443Latency!, _tv8443Candidates!, _tv8443Connections!, _tv8443Error!, _btn8443Toggle!, 18443, _track8443);
            }

            // 防并发重复操作锁
            _btnToggle!.Enabled = !_isActionInProgress;
        }

        private void ApplyChannelStatusToViews(CfnatStatus s, TextView tvState, TextView tvIp, TextView tvLat, TextView tvCand, TextView tvConn, TextView tvErr, Button btnToggle, int port, ChannelTrackState track)
        {
            // 细化 5 级状态流转判定:
            // 1. 正在启动 (Starting / Listening)
            // 2. 已就绪，后台仍在择优 (Ready - Optimizing)
            // 3. 优选完成 (Ready - Optimal)
            // 4. 故障恢复中 (Degraded / Recovering)
            // 5. 已停止 (Stopped)

            string stateStr;
            Color stateColor;

            if (s.State == CfnatState.Stopped)
            {
                stateStr = " 已停止 ";
                stateColor = Color.ParseColor("#616161");
            }
            else if (s.State == CfnatState.Error || s.LastErrorCode != 0)
            {
                stateStr = " 故障恢复中 ";
                stateColor = Color.ParseColor("#FF5252");
            }
            else if (s.State == CfnatState.Starting || s.State == CfnatState.Listening)
            {
                stateStr = " 启动中 ";
                stateColor = Color.ParseColor("#FFB300");
            }
            else if (s.State == CfnatState.Ready)
            {
                if (track.IsFullScanCompleted)
                {
                    stateStr = " 最佳 · READY ";
                    stateColor = Color.ParseColor("#00C853"); // 鲜绿
                }
                else
                {
                    stateStr = " 择优中 · READY ";
                    stateColor = Color.ParseColor("#00E5FF"); // 亮青
                }
            }
            else
            {
                stateStr = " 停止中 ";
                stateColor = Color.ParseColor("#78909C");
            }

            tvState.Text = stateStr;
            tvState.Background = CreatePillDrawable(stateColor);

            // 节点切换与延迟变动追踪
            if (!string.IsNullOrEmpty(s.CurrentSelectedIp))
            {
                if (!string.IsNullOrEmpty(track.LastIp) && s.CurrentSelectedIp != track.LastIp)
                {
                    int oldLat = track.LastLatency;
                    int newLat = s.CurrentLatencyMs;
                    int diff = oldLat - newLat;
                    string diffStr = diff > 0 ? $"降低 {diff}ms" : (diff < 0 ? $"增加 {-diff}ms" : "持平");
                    track.LastUpdateTime = DateTime.Now;

                    // 推送结构化 Info 日志到 UI 瀑布流
                    AppLogBuffer.Append($"cfnat-{port}", "INFO", $"优选节点迭代: {track.LastIp} ({oldLat}ms) -> {s.CurrentSelectedIp} ({newLat}ms) ({diffStr})");
                }
                else if (track.LastUpdateTime == DateTime.MinValue && s.State == CfnatState.Ready)
                {
                    track.LastUpdateTime = DateTime.Now;
                }

                track.LastIp = s.CurrentSelectedIp;
                track.LastLatency = s.CurrentLatencyMs;
            }

            string timeStr = track.LastUpdateTime != DateTime.MinValue ? $" (更新于 {track.LastUpdateTime:HH:mm:ss})" : "";
            tvIp.Text = string.IsNullOrEmpty(s.CurrentSelectedIp) ? "探测中..." : $"{s.CurrentSelectedIp}{timeStr}";
            tvIp.SetTextColor(s.State == CfnatState.Ready ? Color.ParseColor("#00E5FF") : Color.ParseColor("#B0BEC5"));

            tvLat.Text = s.CurrentLatencyMs > 0 ? $"{s.CurrentLatencyMs} ms" : "-- ms";
            if (s.CurrentLatencyMs > 0 && s.CurrentLatencyMs < 300) tvLat.SetTextColor(Color.ParseColor("#69F0AE"));
            else if (s.CurrentLatencyMs >= 300) tvLat.SetTextColor(Color.ParseColor("#FFD54F"));

            tvCand.Text = $"{s.TotalCandidates} 个";
            tvConn.Text = $"{s.ActiveConnections} 个连接 (本地 127.0.0.1:{port})";

            if (s.LastErrorCode != 0)
            {
                tvErr.Text = $"错误[{s.LastErrorCode}]: {s.LastErrorMsg}";
                tvErr.SetTextColor(Color.ParseColor("#FF5252"));
            }
            else
            {
                tvErr.Text = s.State == CfnatState.Ready ? (track.IsFullScanCompleted ? "已锁定最优链路" : "快速通道已就绪，正在后台择优") : "就绪";
                tvErr.SetTextColor(Color.ParseColor("#81C784"));
            }

            btnToggle.Text = (s.State != CfnatState.Stopped) ? "停止实例" : "启动实例";
        }

        private void ResetChannelViews(TextView tvState, TextView tvIp, TextView tvLat, TextView tvCand, TextView tvConn, TextView tvErr, Button btnToggle, int port, ChannelTrackState track)
        {
            track.IsFullScanCompleted = false;
            track.LastIp = string.Empty;
            track.LastLatency = 0;
            track.LastUpdateTime = DateTime.MinValue;

            tvState.Text = " STOPPED ";
            tvState.Background = CreatePillDrawable(Color.ParseColor("#424242"));
            tvIp.Text = "已停止";
            tvIp.SetTextColor(Color.ParseColor("#757575"));
            tvLat.Text = "-- ms";
            tvLat.SetTextColor(Color.ParseColor("#757575"));
            tvCand.Text = "--";
            tvConn.Text = $"0 个连接 (本地 127.0.0.1:{port})";
            tvErr.Text = "实例未加载";
            tvErr.SetTextColor(Color.ParseColor("#757575"));
            btnToggle.Text = "启动实例";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0.0 KB";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }

        #endregion

        #region 用户操作与生命周期联动

        private async void OnToggleClicked()
        {
            if (_isActionInProgress) return;
            _isActionInProgress = true;
            _btnToggle!.Enabled = false;

            try
            {
                var service = TeslVpnService.Instance;
                if (service == null || !service.IsRunning)
                {
                    RequestStartVpn();
                }
                else
                {
                    RequestStopVpn();
                }
            }
            finally
            {
                await Task.Delay(1000);
                _isActionInProgress = false;
                _btnToggle!.Enabled = true;
                UpdateUiFromLiveState();
            }
        }

        private void OnRescanAllClicked()
        {
            var host = TeslVpnService.Instance?.EngineHost;
            if (host != null)
            {
                host.TriggerRescan443();
                host.TriggerRescan8443();
                Toast.MakeText(this, "已下发双通道重新优选测速指令", ToastLength.Short)?.Show();
            }
            else
            {
                Toast.MakeText(this, "服务未运行，无法触发测速", ToastLength.Short)?.Show();
            }
        }

        private void RequestStartVpn()
        {
            Intent? prepareIntent = VpnService.Prepare(this);
            if (prepareIntent != null)
            {
                StartActivityForResult(prepareIntent, RequestVpnPermission);
            }
            else
            {
                StartVpnServiceInternal();
            }
        }

        private void RequestStopVpn()
        {
            var stopIntent = new Intent(this, typeof(TeslVpnService));
            stopIntent.SetAction(TeslVpnService.ActionStop);
            StartService(stopIntent);
            Toast.MakeText(this, "正在停止加速服务...", ToastLength.Short)?.Show();
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode == RequestVpnPermission)
            {
                if (resultCode == Result.Ok)
                {
                    StartVpnServiceInternal();
                }
                else
                {
                    Toast.MakeText(this, "用户取消了 VPN 授权，加速未启动", ToastLength.Long)?.Show();
                }
            }
        }

        private void StartVpnServiceInternal()
        {
            var startIntent = new Intent(this, typeof(TeslVpnService));
            startIntent.SetAction(TeslVpnService.ActionStart);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                StartForegroundService(startIntent);
            }
            else
            {
                StartService(startIntent);
            }
            Toast.MakeText(this, "正在启动 TESL 双通道加速引擎...", ToastLength.Short)?.Show();
        }

        #endregion
    }
}
