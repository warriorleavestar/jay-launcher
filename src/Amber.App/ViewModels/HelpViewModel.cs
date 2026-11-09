using System.Collections.ObjectModel;
using Amber.Core.Help;
using Amber.Core.Localization;
using Amber.Core.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>A block of an article as shown: one of three kinds, plus its links as buttons.</summary>
public sealed record HelpBlockItem(HelpBlock Block)
{
    public string Text => Block.Text;
    public bool IsHeading => Block.Kind == HelpBlockKind.Heading;
    public bool IsParagraph => Block.Kind == HelpBlockKind.Paragraph;
    public bool IsBullet => Block.Kind == HelpBlockKind.Bullet;
    public IReadOnlyList<HelpLink> Links => Block.Links;
    public bool HasLinks => Block.Links.Count > 0;
}

/// <summary>A row in a list of articles (search results, the catalog, related).</summary>
public sealed record HelpArticleItem(HelpArticle Article, string CategoryTitle)
{
    public string Id => Article.Id;
    public string Title => Article.Title;
    public string Summary => Article.Summary;
}

/// <summary>An article ready to show.</summary>
public sealed record HelpArticleView(HelpArticle Article, IReadOnlyList<HelpBlockItem> Blocks, IReadOnlyList<HelpArticleItem> Related)
{
    public string Title => Article.Title;
    public bool HasRelated => Related.Count > 0;
}

/// <summary>
/// Help (official design §6.24, §9.7): the drawer (F1, this page's article, search, related, "Open help center") and
/// the data the help center page reads. Articles are bundled with Jay and read offline.
/// </summary>
public sealed partial class HelpViewModel : ObservableObject
{
    public HelpViewModel()
    {
        Library = HelpLibrary.Load(L.Language);
        L.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(Reload);
    }

    public HelpLibrary Library { get; private set; }

    /// <summary>Opens a route in Jay (jay: links, "Open help center").</summary>
    public Action<string>? Navigate { get; set; }

    [ObservableProperty] public partial bool IsOpen { get; set; }

    /// <summary>The article shown in the drawer.</summary>
    [ObservableProperty] public partial HelpArticleView? Article { get; set; }

    /// <summary>"Help · Mods": the page the drawer was opened for.</summary>
    [ObservableProperty] public partial string Header { get; set; } = "";

    [ObservableProperty] public partial string Query { get; set; } = "";

    public ObservableCollection<HelpArticleItem> Results { get; } = [];

    public bool HasQuery => Query.Trim().Length > 0;
    public bool ShowArticle => !HasQuery && Article is not null;
    public bool NoResults => HasQuery && Results.Count == 0;

    partial void OnQueryChanged(string value) => RunSearch();
    partial void OnArticleChanged(HelpArticleView? value) => OnPropertyChanged(nameof(ShowArticle));

    private void RunSearch()
    {
        Results.Clear();
        if (HasQuery) foreach (var a in Library.Search(Query).Take(30)) Results.Add(Item(a));
        OnPropertyChanged(nameof(HasQuery));
        OnPropertyChanged(nameof(ShowArticle));
        OnPropertyChanged(nameof(NoResults));
    }

    public HelpArticleItem Item(HelpArticle a) => new(a, CategoryTitle(a.Category));

    public static string CategoryTitle(string category) => L.T(category switch
    {
        "start" => "help.category.start",
        "play" => "help.category.play",
        "content" => "help.category.content",
        "worlds" => "help.category.worlds",
        "trouble" => "help.category.trouble",
        "multiplayer" => "help.category.multiplayer",
        "appearance" => "help.category.appearance",
        "storage" => "help.category.storage",
        "privacy" => "help.category.privacy",
        "parents" => "help.category.parents",
        _ => "help.category.reference",
    });

    public HelpArticleView? View(string? id) => Library.Get(id) is { } a
        ? new HelpArticleView(a, [.. a.Blocks.Select(b => new HelpBlockItem(b))], [.. a.Related.Select(Library.Get).OfType<HelpArticle>().Select(Item)])
        : null;

    /// <summary>F1: the drawer with the article for this route.</summary>
    public void OpenFor(Route route, string pageTitle)
    {
        Query = "";
        Article = View(Library.ForRoute(route)?.Id);
        Header = L.F("help.drawer.title", pageTitle);
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    [RelayCommand]
    private void OpenArticle(object? target)
    {
        var id = target switch { HelpArticleItem item => item.Id, HelpLink link => link.Value, string s => s, _ => null };
        if (View(id) is not { } view) return;
        Article = view;
        Header = L.T("help.title");
        Query = "";
    }

    /// <summary>A link inside an article: another article, or a place in Jay (the drawer closes).</summary>
    [RelayCommand]
    private void FollowLink(HelpLink? link)
    {
        if (link is null) return;
        if (link.IsArticle) { OpenArticle(link); return; }
        if (link.IsRoute) { IsOpen = false; Navigate?.Invoke(link.Value); }
    }

    /// <summary>Shift+F1 or "Open help center ›": the help center, on the drawer's article when there is one.</summary>
    [RelayCommand]
    public void OpenCenter()
    {
        IsOpen = false;
        Navigate?.Invoke(Article is { } a ? "help/" + a.Article.Id : "help");
    }

    private void Reload()
    {
        var id = Article?.Article.Id;
        Library = HelpLibrary.Load(L.Language);
        Article = View(id);
        RunSearch();
        Reloaded?.Invoke();
    }

    /// <summary>Raised after the articles were reloaded for another language.</summary>
    public event Action? Reloaded;
}

/// <summary>A catalog group in the help center: a category and its articles.</summary>
public sealed record HelpCategoryGroup(string Title, IReadOnlyList<HelpArticleItem> Articles);

/// <summary>
/// Help center (route <c>help/{article}</c>): the full catalog with search. Wide: catalog on the left, article 640 wide;
/// medium: the catalog becomes a menu; compact: the catalog first, then the article as a sub-page.
/// </summary>
public sealed partial class HelpCenterViewModel : PageViewModel
{
    public HelpCenterViewModel(HelpViewModel help)
    {
        Help = help;
        help.Reloaded += Rebuild;
        Rebuild();
    }

    public HelpViewModel Help { get; }

    public override string Title => L.T("help.center.title");
    public override string Icon => "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm-.1 15h.01M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3";

    public ObservableCollection<HelpCategoryGroup> Groups { get; } = [];

    /// <summary>Every article in catalog order (the medium-width menu).</summary>
    public ObservableCollection<HelpArticleItem> Articles { get; } = [];

    [ObservableProperty] public partial HelpArticleItem? Selected { get; set; }
    [ObservableProperty] public partial HelpArticleView? Article { get; set; }
    [ObservableProperty] public partial string Query { get; set; } = "";

    /// <summary>Compact width: showing an article (otherwise the catalog).</summary>
    [ObservableProperty] public partial bool ArticleEntered { get; set; }
    [ObservableProperty] public partial bool IsCompact { get; set; }

    public ObservableCollection<HelpArticleItem> Results { get; } = [];
    public bool HasQuery => Query.Trim().Length > 0;
    public bool NoResults => HasQuery && Results.Count == 0;

    partial void OnQueryChanged(string value)
    {
        Results.Clear();
        if (HasQuery) foreach (var a in Help.Library.Search(value)) Results.Add(Help.Item(a));
        OnPropertyChanged(nameof(HasQuery));
        OnPropertyChanged(nameof(NoResults));
    }

    partial void OnSelectedChanged(HelpArticleItem? value)
    {
        if (value is null) return;
        Article = Help.View(value.Id);
    }

    /// <summary>help/{id}: show that article (an unknown id shows the first one).</summary>
    public void ShowArticle(string? id)
    {
        var item = Articles.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        Selected = item ?? Articles.FirstOrDefault();
        ArticleEntered = item is not null;
    }

    [RelayCommand]
    private void Open(object? target)
    {
        var id = target switch { HelpArticleItem item => item.Id, HelpLink link => link.Value, string s => s, _ => null };
        if (id is null) return;
        Query = "";
        ShowArticle(id);
    }

    [RelayCommand]
    private void FollowLink(HelpLink? link)
    {
        if (link is null) return;
        if (link.IsArticle) Open(link);
        else if (link.IsRoute) Help.Navigate?.Invoke(link.Value);
    }

    [RelayCommand]
    private void BackToCatalog() => ArticleEntered = false;

    private void Rebuild()
    {
        var id = Selected?.Id;
        Groups.Clear();
        Articles.Clear();
        foreach (var g in Help.Library.Catalog.GroupBy(a => a.Category))
        {
            var items = g.Select(Help.Item).ToList();
            Groups.Add(new HelpCategoryGroup(HelpViewModel.CategoryTitle(g.Key), items));
            foreach (var i in items) Articles.Add(i);
        }
        Selected = Articles.FirstOrDefault(a => a.Id == id) ?? Articles.FirstOrDefault();
        OnQueryChanged(Query);
        OnPropertyChanged(nameof(Title));
    }
}
