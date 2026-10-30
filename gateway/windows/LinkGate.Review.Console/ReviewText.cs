using System.Globalization;
using System.Text.Json;
using LinkGate.Broker;

namespace LinkGate.Review;

public static class ReviewText
{
    public const string Boundary="开发审核：只保存声明候选，不授予网络权限。应用名称/ID自报；二进制签名身份尚未核验，调用范围按 SID+EXE 路径划分。";
    public static string Quote(string value)=>JsonSerializer.Serialize(value);
    public static string Manifest(LinkGate.Contracts.AppManifest manifest)
    {
        using var document=JsonDocument.Parse(manifest.ToJson());
        return JsonSerializer.Serialize(document.RootElement,new JsonSerializerOptions{WriteIndented=true});
    }
    public static IEnumerable<string> Snapshot(ApplicationReviewSnapshot value)
    {
        yield return Boundary;
        yield return "应用引用："+value.ApplicationRef;
        yield return "调用范围摘要（不是二进制认证）："+Quote(value.CallerScope);
        yield return $"待审核修订：{Number(value.PendingRevision)}；决定版本：{Number(value.DecisionVersion)}；上次审核修订：{Number(value.ReviewedRevision)}";
        yield return "上次决定："+value.LastDecision+"；批准修订："+(value.ApprovedRevision is long revision?Number(revision):"none");
        yield return "待审核完整声明：";yield return Manifest(value.PendingManifest);
        yield return "先前批准完整声明：";yield return value.ApprovedManifest is null?"none":Manifest(value.ApprovedManifest);
    }
    public static string Historical(ApplicationReviewResult value)=>
        $"历史结果（不代表当前批准）：{value.ApplicationRef} pending={Number(value.PendingRevision)} version={Number(value.DecisionVersion)} decision={value.Decision}";
    public static string Number(long value)=>value.ToString(CultureInfo.InvariantCulture);
}
