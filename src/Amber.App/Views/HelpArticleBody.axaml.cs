using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Amber.App.Views;

/// <summary>Shows one help article; the host says what a link and a related article do.</summary>
public partial class HelpArticleBody : UserControl
{
    public static readonly StyledProperty<ICommand?> LinkCommandProperty = AvaloniaProperty.Register<HelpArticleBody, ICommand?>(nameof(LinkCommand));
    public static readonly StyledProperty<ICommand?> ArticleCommandProperty = AvaloniaProperty.Register<HelpArticleBody, ICommand?>(nameof(ArticleCommand));

    public ICommand? LinkCommand { get => GetValue(LinkCommandProperty); set => SetValue(LinkCommandProperty, value); }
    public ICommand? ArticleCommand { get => GetValue(ArticleCommandProperty); set => SetValue(ArticleCommandProperty, value); }

    public HelpArticleBody() => InitializeComponent();
}
