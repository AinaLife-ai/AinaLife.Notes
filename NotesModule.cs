using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.QChat;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace AinaLife.Notes;

public class NotesConfig
{
    [DisplayName("自动纸条间隔（小时）")]
    [Description("每隔多少小时自动生成一张温柔纸条，填 0 表示关闭自动纸条")]
    public double AutoIntervalHours { get; set; } = 3;

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
        if (Configuration.AutoIntervalHours <= 0 || generating)
            return;

        DateTime now = DateTime.Now;
        TimeSpan interval = TimeSpan.FromHours(Configuration.AutoIntervalHours);
        if (State.LastAutoTime != default && now - State.LastAutoTime < interval)
            return;

        generating = true;
        try
        {
            //先记录时间，防止生成过程中重复触发
            State.LastAutoTime = now;
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
        using SKBitmap bitmap = new(width, height);
        using (SKCanvas canvas = new(bitmap))
        {
            //纸张底色
            canvas.Clear(new SKColor(0xFF, 0xFB, 0xF0));

            //蓝色横线
            using SKPaint linePaint = new()
            {
                Color = new SKColor(0x9E, 0xC5, 0xF5),
                StrokeWidth = 2,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };
            for (float y = 100; y < height - 30; y += 40)
                canvas.DrawLine(34, y, width - 34, y, linePaint);

            //左侧孔洞（白色填充 + 浅灰描边）
            using SKPaint holeFill = new() { Color = SKColors.White, Style = SKPaintStyle.Fill };
            using SKPaint holeEdge = new()
            {
                Color = new SKColor(0xD0, 0xD0, 0xD0),
                StrokeWidth = 1.5f,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };
            for (float y = 120; y < height - 30; y += 40)
            {
                canvas.DrawCircle(24, y, 7, holeFill);
                canvas.DrawCircle(24, y, 7, holeEdge);
            }

            //标题
            using SKPaint titlePaint = new()
            {
                Color = new SKColor(0x8A, 0x6D, 0x6D),
                TextSize = 26,
                IsAntialias = true,
                Typeface = GetTypeface()
            };
            canvas.DrawText(isAuto ? "温柔小纸条" : "小纸条", 60, 62, titlePaint);

            //标题右侧小爱心
            using SKPaint heartPaint = new() { Color = new SKColor(0xE8, 0x5D, 0x75), Style = SKPaintStyle.Fill, IsAntialias = true };
            using (SKPath smallHeart = CreateHeartPath(190, 42, 26))
                canvas.DrawPath(smallHeart, heartPaint);

            //正文（自动换行，最多4行）
            using SKPaint textPaint = new()
            {
                Color = new SKColor(0x44, 0x44, 0x44),
                TextSize = 34,
                IsAntialias = true,
                Typeface = GetTypeface()
            };
            string[] lines = WrapText(content, textPaint, width - 150);
            float textY = 152;
            foreach (string line in lines.Take(4))
            {
                canvas.DrawText(line, 70, textY, textPaint);
                textY += 46;
            }
            if (lines.Length > 4)
                canvas.DrawText("……", 70, textY, textPaint);

            //右下角大爱心
            using (SKPath bigHeart = CreateHeartPath(width - 80, 96, 52))
                canvas.DrawPath(bigHeart, heartPaint);

            //签名
            string signature = string.IsNullOrWhiteSpace(Configuration.Signature)
                ? "爱奈丽"
                : Configuration.Signature.Trim();
            using SKPaint signPaint = new()
            {
                Color = new SKColor(0x3A, 0x6E, 0xC8),
                TextSize = 22,
                IsAntialias = true,
                Typeface = GetTypeface()
            };
            canvas.DrawText($"—— {signature}", width - 150 - signPaint.MeasureText(signature), height - 32, signPaint);
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.OpenWrite(file);
        data.SaveTo(stream);
        return file;
    }

    /// <summary>按画布宽度逐字换行</summary>
    private static string[] WrapText(string text, SKPaint paint, float maxWidth)
    {
        List<string> lines = new();
        string current = "";
        foreach (char c in text)
        {
            string test = current + c;
            if (paint.MeasureText(test) > maxWidth && current.Length > 0)
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

    /// <summary>标准贝塞尔心形路径</summary>
    private static SKPath CreateHeartPath(float cx, float cy, float size)
    {
        SKPath path = new();
        path.MoveTo(cx, cy + size * 0.35f);
        path.CubicTo(cx - size * 0.6f, cy - size * 0.1f, cx - size * 0.5f, cy - size * 0.5f, cx, cy - size * 0.25f);
        path.CubicTo(cx + size * 0.5f, cy - size * 0.5f, cx + size * 0.6f, cy - size * 0.1f, cx, cy + size * 0.35f);
        path.Close();
        return path;
    }

    /// <summary>优先使用手写感的中文字体</summary>
    private static SKTypeface GetTypeface()
    {
        string[] candidates = { "KaiTi", "楷体", "STKaiti", "FangSong", "SimSun", "Microsoft YaHei" };
        foreach (string name in candidates)
        {
            SKTypeface typeface = SKTypeface.FromFamilyName(name);
            if (typeface.FamilyName.Contains(name, StringComparison.OrdinalIgnoreCase))
                return typeface;
        }
        return SKTypeface.Default;
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
