using System.Text.Json.Serialization;
namespace Sokna.PrintAgent.Core;
public sealed record DestinationConfig([property:JsonPropertyName("destination_key")]string DestinationKey,[property:JsonPropertyName("label")]string Label,[property:JsonPropertyName("windows_queue_name")]string WindowsQueueName,[property:JsonPropertyName("paper_width_mm")]double PaperWidthMm,[property:JsonPropertyName("printable_width_mm")]double PrintableWidthMm,[property:JsonPropertyName("copies")]int Copies,[property:JsonPropertyName("layout_mode")]string LayoutMode);
public sealed record ClaimedJob(int Id,string PublicToken,string JobType,bool Required,string? EntityType,string? EntityId,string CreatedAt,int ContractVersion,string ContentSha256,string PayloadJson);
public sealed record ClaimAttempt(long Id,int AttemptNo,string LeaseToken,string LeaseExpiresAt);
public sealed record ClaimItem(ClaimedJob Job,ClaimAttempt Attempt,DestinationConfig Destination);
public sealed record ClaimResponse(bool Success,string RequestId,List<ClaimItem> Jobs,string ServerTime,bool Idempotent);
public sealed record ApiResult(bool Success,string? Status=null,bool Idempotent=false,string? Code=null,string? Message=null,bool RequiresHumanResolution=false);
public enum LocalJobState{Reserved,Claimed,WorkerLaunching,Submitted,ReportPending,SafeFailed,Unknown,RecoveryHold,Resolved}
public sealed record LocalJob(long ServerJobId,long AttemptId,int AttemptNo,string DestinationKey,string QueueName,double PaperWidthMm,double PrintableWidthMm,int Copies,string LayoutMode,string PayloadJson,string ContentSha256,string LocalReceiptId,string ProtectedLeaseToken,DateTimeOffset LeaseExpiresAt,LocalJobState State,string? SpoolerJobId,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt,DateTimeOffset? WorkerLaunchingAt,string? LastError);
public sealed record WorkerInput(long ServerJobId,long AttemptId,string LocalReceiptId,string QueueName,string PayloadJson,string ContentSha256,double PaperWidthMm,double PrintableWidthMm,int Copies,string ResultPath,string FencePath,string StartSignalPath);
public sealed record WorkerResult(long ServerJobId,long AttemptId,string LocalReceiptId,string ContentSha256,string Status,string? SpoolerJobId=null,bool Retryable=false,string? ErrorCode=null,string? ErrorMessage=null);
public sealed record PrinterQueueHealth(string Name,bool Offline,bool Paused,bool PaperOut,bool Error,int Jobs,string Driver,string Port);
public sealed record LocalHealthSnapshot(string AgentVersion,string Hostname,string State,bool ConfigOk,bool SecretOk,bool ServiceAccountContext,string? LastError,string UpdatedAt,int LocalBacklogCount,int LocalUnknownCount,List<PrinterQueueHealth> Printers);
