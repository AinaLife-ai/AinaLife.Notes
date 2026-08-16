using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace AinaLife.Notes;

public class NotesConfig
{
    [DisplayName("自动纸条间隔（小时）")]
    [Description("每隔多少小时自动生成一张温柔纸条，填 0 表示关闭自动纸条")]
    public double AutoIntervalHours { get; set; } = 3;

    [DisplayName("最大纸条数量")]
    [Description("纸条最多保留多少张，超出自动丢弃最旧的")]
    public int MaxNotes { get; set; } = 100;
}

public class NoteItem
{
    public string Content { get; set; } = "";
    public string Time { get; set; } = "";
    public bool IsAuto { get; set; }
}

public class NotesState
{
    public List<NoteItem> Notes { get; set; } = new();
    public DateTime LastAutoTime { get; set; }
}

[Module("温柔纸条",
    "可以留纸条、查看纸条、删除纸条，还能每隔一段时间自动生成一张温柔纸条",
    defaultCategory: "AinaLife/实用")]
public class NotesModule(
    XmlFunctionCaller functionCaller,
    ILogger<NotesModule> logger,
    Interactor<NotesModule> interactor,
    StorageSystem storageSystem
) : ChatBehaviour, IConfigurable<NotesConfig>
{
    public NotesConfig Configuration { get; set; } = null!;

    private static readonly string[] WarmWords =
    {
        "记得喝水呀，你的嗓子会感谢你的",
        "忙累了就歇会儿，世界又不差这几分钟",
        "今天也辛苦啦，你已经做得很好了",
        "窗外的风很温柔，你也是",
        "按时吃饭，别让胃陪你加班",
        "别总盯着屏幕，抬头看看远处的绿",
        "想做的事慢慢来，稳稳的就好",
        "你比你以为的，要可爱得多",
        "✨ 叮！一张惊喜纸条：今天也要元气满满哦",
        "🌙 慢慢来，比较快，你比想象中厉害"
    };

    private const string StatePath = "Notes/state";

    private NotesState State { get; set; } = new();

    [XmlFunction(FunctionMode.OneShot)]
    [Description("留下一张纸条（可以是给用户的温柔话、提醒、心情、待办等），会保存到纸条列表")]
    public Task AddNote([Description("纸条内容")] string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new Exception("纸条内容不能为空");

        State.Notes.Insert(0, new NoteItem
        {
            Content = content.Trim(),
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            IsAuto = false
        });
        TrimNotes();
        SaveState();
        interactor.Poke($"已留好一张纸条：{content.Trim()}");
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看当前所有纸条，返回带序号的列表")]
    public Task<string> ListNotes()
    {
        if (State.Notes.Count == 0)
            return Task.FromResult("还没有纸条，可以用 AddNote 留一张");

        return Task.FromResult(string.Join("\n",
            State.Notes.Select((n, i) => $"{i + 1}. [{n.Time}] {n.Content}")));
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("删除第几张纸条，序号从 1 开始（可用 ListNotes 查看序号）")]
    public Task DeleteNote([Description("纸条序号，从 1 开始")] int index)
    {
        if (index < 1 || index > State.Notes.Count)
            throw new Exception($"纸条序号无效，当前共有 {State.Notes.Count} 张纸条");

        var removed = State.Notes[index - 1];
        State.Notes.RemoveAt(index - 1);
        SaveState();
        interactor.Poke($"已删除纸条：{removed.Content}");
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("清空所有纸条")]
    public Task ClearNotes()
    {
        int count = State.Notes.Count;
        State.Notes.Clear();
        SaveState();
        interactor.Poke(count > 0 ? $"已清空 {count} 张纸条" : "纸条本来就是空的");
        return Task.CompletedTask;
    }

    protected override Task OnAwake()
    {
        State = storageSystem.GetObject<NotesState>(StatePath, new NotesState()) ?? new NotesState();

        XmlHandler xmlHandler = new(this)
        {
            Description = "温柔纸条服务：可以留纸条、查看纸条、删除纸条，并支持自动生成温柔纸条。",
            Explanation = "AddNote 留纸条；ListNotes 查看；DeleteNote 删除；ClearNotes 清空。自动纸条间隔由模块配置控制。"
        };
        functionCaller.RegisterHandler(xmlHandler,
            DocumentMode.Implicit,
            cancellationToken: DestroyCancellationToken);

        return Task.CompletedTask;
    }

    protected override Task OnUpdate()
    {
        if (Configuration.AutoIntervalHours <= 0)
            return Task.CompletedTask;

        DateTime now = DateTime.Now;
        TimeSpan interval = TimeSpan.FromHours(Configuration.AutoIntervalHours);
        if (State.LastAutoTime == default || now - State.LastAutoTime >= interval)
        {
            State.LastAutoTime = now;
            string word = WarmWords[Random.Shared.Next(WarmWords.Length)];
            State.Notes.Insert(0, new NoteItem
            {
                Content = word,
                Time = now.ToString("yyyy-MM-dd HH:mm"),
                IsAuto = true
            });
            TrimNotes();
            SaveState();
            interactor.Poke($"♡ 温柔小纸条：{word}");
        }

        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        return Task.CompletedTask;
    }

    private void TrimNotes()
    {
        if (Configuration.MaxNotes > 0 && State.Notes.Count > Configuration.MaxNotes)
            State.Notes.RemoveRange(Configuration.MaxNotes, State.Notes.Count - Configuration.MaxNotes);
    }

    private void SaveState()
    {
        try
        {
            storageSystem.SetObject(StatePath, State);
        }
        catch (Exception ex)
        {
            logger.LogWarning("纸条状态保存失败：{message}", ex.Message);
        }
    }
}
