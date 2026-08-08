using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace AISky_Desktop;

public sealed partial class TutorialDialog : ContentDialog
{
    private static readonly TutorialStep[] Steps =
    [
        new(
            "\uE787",
            "选择预报",
            "先确定要浏览的模型、起报和预报时次。",
            [
                "顶部选择模型、起报时刻和预报时效。",
                "时间轴每 8 格为一组（一天）；点击直达，播放可连续浏览 120 个时次。",
                "左右方向键切换时次，空格键播放或暂停。",
            ]),
        new(
            "\uE707",
            "探索地图",
            "平移、缩放和点选都直接发生在地图上。",
            [
                "拖动平移、滚轮缩放；“全球”恢复视图并支持东西循环拖动。",
                "点击格点打开左侧序列；悬停曲线预览，点击曲线点跳转时刻。",
                "左下角显示经纬度和数值，经纬网会随缩放调整疏密。",
            ]),
        new(
            "\uE8A5",
            "变量与色带",
            "右侧负责选择产品，右下角负责解释颜色。",
            [
                "右侧可搜索和分类筛选变量，点击变量行切换产品。",
                "点击色带选择 50 套方案或反转，并按变量分别记忆。",
                "范围和单位采用产品表，无量纲变量不显示单位。",
            ]),
        new(
            "\uE9CA",
            "风场与台风",
            "流线和模式模拟路径都是可独立开关的叠加层。",
            [
                "风场流线展示风向与速度，粒子快慢和长度随风速变化。",
                "台风路径显示未来 5 天；当前模型置顶，另一模型变灰。",
                "悬停查看强度，点击路径点跳转时刻，并显示 24/48 小时警戒线。",
            ]),
        new(
            "\uE895",
            "同步与数据",
            "下载在后台完成，不影响继续浏览地图。",
            [
                "自动同步定时检查两个模型；点击状态卡片展开独立进度与速度。",
                "“…”中可立即同步、检查更新或导入 NetCDF；“补数”可后台回溯并取消。",
                "设置中可修改密码和数据目录；切换目录会迁移数据并重启。",
            ]),
        new(
            "\uE713",
            "显示与后台",
            "常用个性化和后台行为都集中在“…”菜单。",
            [
                "默认 UTC，可切换 UTC+8 等时区；深色界面同步调整地图和仪表。",
                "关闭窗口后可留在托盘同步，再次启动只唤醒同一个实例。",
                "设置可管理开机启动、托盘和更新；“…”可随时重开教程。",
            ]),
    ];

    private int _stepIndex;

    public TutorialDialog(bool showTutorialOnOpen)
    {
        InitializeComponent();
        DoNotShowAgainCheckBox.IsChecked = !showTutorialOnOpen;
        UpdateStep(animate: false);
    }

    public ObservableCollection<string> BulletItems { get; } = [];

    public bool SuppressAutomaticOpening =>
        DoNotShowAgainCheckBox.IsChecked == true;

    private void ContentDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        if (_stepIndex >= Steps.Length - 1)
        {
            return;
        }

        args.Cancel = true;
        _stepIndex++;
        UpdateStep(animate: true);
    }

    private void ContentDialog_SecondaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_stepIndex == 0)
        {
            return;
        }

        _stepIndex--;
        UpdateStep(animate: true);
    }

    private void UpdateStep(bool animate)
    {
        var step = Steps[_stepIndex];
        TutorialStepIcon.Glyph = step.Glyph;
        TutorialStepTitle.Text = step.Title;
        TutorialStepDescription.Text = step.Description;
        TutorialStepCounter.Text = $"{_stepIndex + 1} / {Steps.Length}";
        TutorialProgress.Maximum = Steps.Length;
        TutorialProgress.Value = _stepIndex + 1;
        BulletItems.Clear();
        foreach (var bullet in step.Bullets)
        {
            BulletItems.Add(bullet);
        }

        var illustrations = new UIElement[]
        {
            ForecastIllustration,
            MapIllustration,
            VariableIllustration,
            WindIllustration,
            SyncIllustration,
            DisplayIllustration,
        };
        for (var index = 0; index < illustrations.Length; index++)
        {
            illustrations[index].Visibility = index == _stepIndex
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        IsSecondaryButtonEnabled = _stepIndex > 0;
        PrimaryButtonText = _stepIndex == Steps.Length - 1
            ? "完成"
            : "下一步";
        if (animate)
        {
            PlayStepTransition();
        }
    }

    private void PlayStepTransition()
    {
        TutorialStepContent.Opacity = 0;
        TutorialStepTransform.X = 12;
        var easing = new ExponentialEase
        {
            Exponent = 5,
            EasingMode = EasingMode.EaseOut,
        };
        var storyboard = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = easing,
        };
        Storyboard.SetTarget(fade, TutorialStepContent);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);
        var slide = new DoubleAnimation
        {
            From = 12,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(210),
            EasingFunction = easing,
        };
        Storyboard.SetTarget(slide, TutorialStepTransform);
        Storyboard.SetTargetProperty(slide, "X");
        storyboard.Children.Add(slide);
        storyboard.Begin();
    }

    private sealed record TutorialStep(
        string Glyph,
        string Title,
        string Description,
        IReadOnlyList<string> Bullets);
}
