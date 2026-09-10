using System.Text;
using System.Text.Json;

namespace Sokna.PrintAgent.Core;

public sealed record PreviewSafetyLimits(
    int MaxPayloadBytes,
    int MaxTextCharacters,
    int MaxItems,
    int MaxHeightPixels,
    long MaxPixelArea,
    int MaxOutputBytes)
{
    public static readonly PreviewSafetyLimits AbsoluteMaximum=new(
        262144,
        200000,
        1000,
        48000,
        48000000,
        8000000);

    public PreviewSafetyLimits ClampToAbsolute()
    {
        var hard=AbsoluteMaximum;
        return new(
            Math.Min(MaxPayloadBytes,hard.MaxPayloadBytes),
            Math.Min(MaxTextCharacters,hard.MaxTextCharacters),
            Math.Min(MaxItems,hard.MaxItems),
            Math.Min(MaxHeightPixels,hard.MaxHeightPixels),
            Math.Min(MaxPixelArea,hard.MaxPixelArea),
            Math.Min(MaxOutputBytes,hard.MaxOutputBytes));
    }
}

public sealed record PreviewPayloadMetrics(
    int PayloadBytes,
    long TextCharacters,
    int ItemCount,
    int WidthPixels,
    int MaximumSafeHeightPixels);

/// <summary>
/// Pure, spooler-free guard used on both sides of the preview process boundary. It rejects
/// unreasonable document complexity and computes the maximum allocatable raster height before
/// the renderer creates a receipt-sized bitmap. Worker-side validation remains authoritative.
/// </summary>
public static class PreviewSafety
{
    public static PreviewPayloadMetrics Validate(
        string payloadJson,
        double paperWidthMm,
        double printableWidthMm,
        int dpiX,
        int dpiY,
        PreviewSafetyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        ArgumentNullException.ThrowIfNull(limits);
        limits=limits.ClampToAbsolute();
        ValidateLimits(limits);

        if(!double.IsFinite(paperWidthMm)||paperWidthMm is not (58d or 80d))
            throw new InvalidDataException("Preview paper width معتبر نیست.");
        if(!double.IsFinite(printableWidthMm)||printableWidthMm<20||printableWidthMm>paperWidthMm)
            throw new InvalidDataException("Preview printable width معتبر نیست.");
        if(dpiX is <100 or >600||dpiY is <100 or >600)
            throw new InvalidDataException("Preview DPI معتبر نیست.");

        var payloadBytes=Encoding.UTF8.GetByteCount(payloadJson);
        if(payloadBytes<2||payloadBytes>limits.MaxPayloadBytes)
            throw new InvalidDataException("Preview payload از سقف bytes مجاز عبور کرده است.");

        using var document=JsonDocument.Parse(payloadJson,new JsonDocumentOptions{MaxDepth=64,CommentHandling=JsonCommentHandling.Disallow,AllowTrailingCommas=false});
        if(document.RootElement.ValueKind!=JsonValueKind.Object)
            throw new InvalidDataException("Preview payload باید JSON object باشد.");

        long textCharacters=0;
        var itemCount=0;
        Count(document.RootElement,ref textCharacters,ref itemCount,limits);

        var widthPixels=Math.Max(32,checked((int)Math.Round(printableWidthMm/25.4*dpiX)));
        var areaBound=(int)Math.Min(int.MaxValue,limits.MaxPixelArea/Math.Max(1,widthPixels));
        var maximumSafeHeight=Math.Min(limits.MaxHeightPixels,areaBound);
        var minimumUsefulHeight=Math.Max(160,checked((int)Math.Round(160*dpiY/203d)));
        if(maximumSafeHeight<minimumUsefulHeight)
            throw new InvalidDataException("Preview raster budget برای این DPI/عرض کافی نیست.");

        return new(payloadBytes,textCharacters,itemCount,widthPixels,maximumSafeHeight);
    }

    private static void Count(JsonElement element,ref long textCharacters,ref int itemCount,PreviewSafetyLimits limits)
    {
        switch(element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach(var property in element.EnumerateObject())
                {
                    textCharacters+=property.Name.Length;
                    CheckText(textCharacters,limits);
                    if(property.NameEquals("items")&&property.Value.ValueKind==JsonValueKind.Array)
                    {
                        itemCount=checked(itemCount+property.Value.GetArrayLength());
                        if(itemCount>limits.MaxItems)throw new InvalidDataException("Preview item count از سقف مجاز عبور کرده است.");
                    }
                    Count(property.Value,ref textCharacters,ref itemCount,limits);
                }
                break;
            case JsonValueKind.Array:
                foreach(var item in element.EnumerateArray())Count(item,ref textCharacters,ref itemCount,limits);
                break;
            case JsonValueKind.String:
                textCharacters+=(element.GetString()??string.Empty).Length;
                CheckText(textCharacters,limits);
                break;
        }
    }

    private static void CheckText(long count,PreviewSafetyLimits limits)
    {
        if(count>limits.MaxTextCharacters)throw new InvalidDataException("Preview text complexity از سقف مجاز عبور کرده است.");
    }

    private static void ValidateLimits(PreviewSafetyLimits limits)
    {
        if(limits.MaxPayloadBytes<2||limits.MaxTextCharacters<1||limits.MaxItems<1||limits.MaxHeightPixels<160||limits.MaxPixelArea<32000||limits.MaxOutputBytes<1024)
            throw new InvalidDataException("Preview safety limits معتبر نیستند.");
    }
}
