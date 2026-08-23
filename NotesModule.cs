using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.QChat;
using Microsoft.Extensions.Logging;

namespace AinaLife.Notes;

public class NotesConfig
{
    [DisplayName("自动纸条最小间隔（小时）")]
    [Description("自动纸条的最小间隔，与最大间隔构成随机范围，填 0 表示关闭自动纸条")]
    public double AutoIntervalMinHours { get; set; } = 2;

    [DisplayName("自动纸条最大间隔（小时）")]
    [Description("自动纸条的最大间隔，与最小间隔构成随机范围，填 0 表示关闭自动纸条")]
    public double AutoIntervalMaxHours { get; set; } = 5;

    [DisplayName("最大纸条数量")]
    [Description("纸条最多保留多少张，超出自动丢弃最旧的")]
    public int MaxNotes { get; set; } = 100;

    [DisplayName("纸条发送类型")]
    [Description("纸条渲染成便条图片后发送到哪：Group=群聊，Private=私聊，None=仅存档不发送")]
    public string AutoSendType { get; set; } = "None";

    [DisplayName("纸条发送目标")]
    [Description("纸条图片发送目标：群号或QQ号，配合发送类型使用，填 0 表示不发送")]
    public long AutoSendTargetId { get; set; }

    [DisplayName("便条签名")]
    [Description("便条图片右下角的手写签名")]
    public string Signature { get; set; } = "爱奈丽";
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
    public DateTime NextAutoTime { get; set; }
}

[Module("温柔纸条",
    "可以留纸条、查看纸条、删除纸条，还能每隔一段时间自动生成一张温柔纸条，并渲染成手写便条图片发送",
    defaultCategory: "AinaLife/实用")]
public class NotesModule(
    XmlFunctionCaller functionCaller,
    ILogger<NotesModule> logger,
    Interactor<NotesModule> interactor,
    StorageSystem storageSystem,
    ChatBot chatBot,
    QChatService? qChatService = null
) : ChatBehaviour, IConfigurable<NotesConfig>
{
    public NotesConfig Configuration { get; set; } = null!;

    private const string StatePath = "Notes/state";
    private bool generating;

    private NotesState State { get; set; } = new();

    [XmlFunction(FunctionMode.OneShot)]
    [Description("留下一张纸条（可以是给用户的温柔话、提醒、心情、待办等），会保存到纸条列表")]
    public async Task AddNote([Description("纸条内容")] string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new Exception("纸条内容不能为空");

        string trimmed = content.Trim();
        State.Notes.Insert(0, new NoteItem
        {
            Content = trimmed,
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            IsAuto = false
        });
        TrimNotes();
        SaveState();
        interactor.Poke($"已留好一张纸条：{trimmed}");
        await TryRenderAndSendAsync(trimmed, false);
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

        //兼容旧状态：只有 LastAutoTime 时，按最大间隔推算下一次
        if (State.NextAutoTime == default && State.LastAutoTime != default)
            State.NextAutoTime = State.LastAutoTime.AddHours(
                Configuration.AutoIntervalMaxHours > 0 ? Configuration.AutoIntervalMaxHours : 3);

        XmlHandler xmlHandler = new(this)
        {
            Description = "温柔纸条服务：可以留纸条、查看纸条、删除纸条，并支持自动生成温柔纸条（AI 自主生成内容，可渲染成手写便条图片发送）。",
            Explanation = "AddNote 留纸条；ListNotes 查看；DeleteNote 删除；ClearNotes 清空。自动纸条间隔与发送目标由模块配置控制。"
        };
        functionCaller.RegisterHandler(xmlHandler,
            DocumentMode.Implicit,
            cancellationToken: DestroyCancellationToken);

        return Task.CompletedTask;
    }

    protected override async Task OnUpdate()
    {
        if (Configuration.AutoIntervalMaxHours <= 0 || generating)
            return;

        DateTime now = DateTime.Now;
        if (State.NextAutoTime != default && now < State.NextAutoTime)
            return;

        generating = true;
        try
        {
            //先随机出下一次时间，防止生成过程中重复触发
            State.NextAutoTime = RollNextTime(now);
            SaveState();

            string prompt =
                "[消息来源(温柔纸条)]现在又到了生成温柔纸条的时间。" +
                "请直接输出一句给用户的暖心话（不要解释、不要寒暄、不要带前缀，直接给纸条内容本身，控制在50字以内，像\"记得喝水呀，你的嗓子会感谢你的\"这样自然的口吻）。";

            ChatResult result = await chatBot.ChatAsync(prompt, breakLast: false);
            if (result.Exception != null)
            {
                logger.LogWarning("自动纸条生成失败：{message}", result.Exception.Message);
                return;
            }

            string content = (result.AIMessage ?? "").Trim().Trim('"', '“', '”', '\n', '\r');
            if (string.IsNullOrWhiteSpace(content))
            {
                logger.LogWarning("自动纸条生成结果为空，跳过本次");
                return;
            }
            if (content.Length > 200)
                content = content[..200];

            State.Notes.Insert(0, new NoteItem
            {
                Content = content,
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                IsAuto = true
            });
            TrimNotes();
            SaveState();
            interactor.Poke($"已自动生成一张温柔纸条并保存：{content}");
            await TryRenderAndSendAsync(content, true);
        }
        finally
        {
            generating = false;
        }
    }

    protected override Task OnDestroy()
    {
        return Task.CompletedTask;
    }

    /// <summary>渲染手写便条图片并发送（未配置发送目标时跳过）</summary>
    private async Task TryRenderAndSendAsync(string content, bool isAuto)
    {
        if (Configuration.AutoSendTargetId <= 0)
            return;
        if (qChatService == null)
        {
            logger.LogWarning("未启用 QQ 聊天模块，无法发送纸条图片");
            return;
        }

        try
        {
            string imagePath = RenderNoteImage(content, isAuto);
            string typeText = (Configuration.AutoSendType ?? "").Trim().ToLower();
            OneBotMessageType type = typeText switch
            {
                "private" => OneBotMessageType.Private,
                _ => OneBotMessageType.Group
            };
            await qChatService.QImage(type, Configuration.AutoSendTargetId, imagePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning("纸条图片渲染或发送失败：{message}", ex.Message);
        }
    }

    /// <summary>渲染一张手写便条样式的图片，返回本地绝对路径</summary>
    private string RenderNoteImage(string content, bool isAuto)
    {
        string dir = Path.Combine(AlifePath.StorageFolderPath, "Notes", "Images");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"note_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

        const int width = 720;
        const int height = 480;
        using Bitmap bitmap = new(width, height);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.FromArgb(0xFF, 0xFB, 0xF0));

            //蓝色横线
            using Pen linePen = new(Color.FromArgb(0x9E, 0xC5, 0xF5), 2f);
            for (float y = 100; y < height - 30; y += 40)
                g.DrawLine(linePen, 34, y, width - 34, y);

            //左侧孔洞（白色填充 + 浅灰描边）
            using Brush holeFill = Brushes.White;
            using Pen holeEdge = new(Color.FromArgb(0xD0, 0xD0, 0xD0), 1.5f);
            for (float y = 120; y < height - 30; y += 40)
            {
                g.FillEllipse(holeFill, 24 - 7, y - 7, 14, 14);
                g.DrawEllipse(holeEdge, 24 - 7, y - 7, 14, 14);
            }

            //标题
            using Font titleFont = new(GetFontFamily(), 26f, FontStyle.Regular, GraphicsUnit.Pixel);
            using Brush titleBrush = new SolidBrush(Color.FromArgb(0x8A, 0x6D, 0x6D));
            g.DrawString(isAuto ? "温柔小纸条" : "小纸条", titleFont, titleBrush, 60, 30);

            //标题右侧小爱心
            using Brush heartBrush = new SolidBrush(Color.FromArgb(0xE8, 0x5D, 0x75));
            using (GraphicsPath smallHeart = CreateHeartPath(190, 42, 26))
                g.FillPath(heartBrush, smallHeart);

            //正文（自动换行，最多4行）
            using Font textFont = new(GetFontFamily(), 34f, FontStyle.Regular, GraphicsUnit.Pixel);
            using Brush textBrush = new SolidBrush(Color.FromArgb(0x44, 0x44, 0x44));
            string[] lines = WrapText(content, textFont, width - 150);
            float textY = 152;
            foreach (string line in lines.Take(4))
            {
                g.DrawString(line, textFont, textBrush, 70, textY);
                textY += 46;
            }
            if (lines.Length > 4)
                g.DrawString("……", textFont, textBrush, 70, textY);

            //右下角大爱心
            using (GraphicsPath bigHeart = CreateHeartPath(width - 80, 96, 52))
                g.FillPath(heartBrush, bigHeart);

            //签名
            string signature = string.IsNullOrWhiteSpace(Configuration.Signature)
                ? "爱奈丽"
                : Configuration.Signature.Trim();
            using Font signFont = new(GetFontFamily(), 22f, FontStyle.Regular, GraphicsUnit.Pixel);
            using Brush signBrush = new SolidBrush(Color.FromArgb(0x3A, 0x6E, 0xC8));
            string signText = $"—— {signature}";
            SizeF signSize = g.MeasureString(signText, signFont);
            g.DrawString(signText, signFont, signBrush, width - 150 - signSize.Width, height - 32);
        }

        bitmap.Save(file, ImageFormat.Png);
        return file;
    }

    /// <summary>按画布宽度逐字换行</summary>
    private static string[] WrapText(string text, Font font, float maxWidth)
    {
        List<string> lines = new();
        string current = "";
        foreach (char c in text)
        {
            string test = current + c;
            if (font.Size > 0 && MeasureText(test, font) > maxWidth && current.Length > 0)
            {
                lines.Add(current);
                current = c.ToString();
            }
            else
            {
                current = test;
            }
        }
        if (current.Length > 0)
            lines.Add(current);
        return lines.Count == 0 ? new[] { text } : lines.ToArray();
    }

    /// <summary>测量文本宽度（Graphics 实例不可跨调用持有，用静态位图测量）</summary>
    private static float MeasureText(string text, Font font)
    {
        using Bitmap bmp = new(1, 1);
        using Graphics g = Graphics.FromImage(bmp);
        return g.MeasureString(text, font).Width;
    }

    /// <summary>标准贝塞尔心形路径</summary>
    private static GraphicsPath CreateHeartPath(float cx, float cy, float size)
    {
        GraphicsPath path = new();
        path.AddBezier(cx - size * 0.6f, cy - size * 0.1f, cx - size * 0.5f, cy - size * 0.5f, cx, cy - size * 0.25f, cx, cy + size * 0.35f);
        path.AddBezier(cx, cy + size * 0.35f, cx + size * 0.5f, cy - size * 0.5f, cx + size * 0.6f, cy - size * 0.1f, cx, cy + size * 0.35f);
        path.CloseFigure();
        return path;
    }

    /// <summary>优先使用手写感的中文字体</summary>
    private static FontFamily GetFontFamily()
    {
        string[] candidates = { "KaiTi", "楷体", "STKaiti", "FangSong", "SimSun", "Microsoft YaHei" };
        foreach (string name in candidates)
        {
            try
            {
                FontFamily family = new(name);
                if (family.IsStyleAvailable(FontStyle.Regular))
                    return family;
                family.Dispose();
            }
            catch
            {
                //字体不存在，尝试下一个
            }
        }
        return FontFamily.GenericSansSerif;
    }

    /// <summary>在配置的随机间隔范围内掷出下一次触发时间</summary>
    private DateTime RollNextTime(DateTime now)
    {
        double min = Configuration.AutoIntervalMinHours;
        double max = Configuration.AutoIntervalMaxHours;
        if (max <= 0)
            return DateTime.MaxValue;
        if (min <= 0)
            min = 0.5;
        if (min > max)
            min = max;
        double hours = min + Random.Shared.NextDouble() * (max - min);
        return now.AddHours(hours);
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