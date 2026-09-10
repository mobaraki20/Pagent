using System.Security.Cryptography;
using System.Text.Json;
using Sokna.PrintAgent.Core;

namespace Sokna.PrintAgent.Service;

/// <summary>
/// Executes the read-only Worker --preview mode under the same guarded child-process lifecycle
/// used by real print workers. It never calls IPrintTransport and never invokes a spool adapter.
/// Input and output evidence are deleted after every proven completion/cancellation path.
/// </summary>
public sealed class SystemPreviewExecutor : IPreviewExecutor
{
    private readonly AgentPaths _paths;
    private readonly WorkerSupervisor _supervisor;
    private readonly AgentLog _log;

    public SystemPreviewExecutor(AgentPaths paths,WorkerSupervisor supervisor,AgentLog log)
    {
        _paths=paths;
        _supervisor=supervisor;
        _log=log;
    }

    public async Task<PreviewScheduleResult> ExecuteAsync(PreviewWorkRequest request,CancellationToken ct)
    {
        var limits=request.SafetyLimits.ClampToAbsolute();
        var metrics=PreviewSafety.Validate(
            request.PayloadJson,
            request.PaperWidthMm,
            request.PrintableWidthMm,
            request.DpiX,
            request.DpiY,
            limits);

        var rendererStagingHeight=Math.Clamp((int)Math.Round(16000*request.DpiY/203d),16000,48000);
        if((long)metrics.WidthPixels*rendererStagingHeight>limits.MaxPixelArea)
            return PreviewScheduleResult.Failed(request,"preview_raster_budget_exceeded");

        var id=Guid.NewGuid().ToString("N");
        var inputPath=Path.Combine(_paths.WorkPath,$"preview-{id}.json");
        var outputPath=Path.Combine(_paths.WorkPath,$"preview-{id}.png");
        var worker=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Worker","Sokna.PrintAgent.Worker.exe"));
        var input=new PreviewWorkerInput(
            request.PayloadJson,
            request.PaperWidthMm,
            request.PrintableWidthMm,
            request.DpiX,
            request.DpiY,
            outputPath,
            limits.MaxPayloadBytes,
            limits.MaxTextCharacters,
            limits.MaxItems,
            limits.MaxHeightPixels,
            limits.MaxPixelArea,
            limits.MaxOutputBytes);

        await DurableFile.WriteJsonAtomicAsync(inputPath,input,ct);
        try
        {
            var spec=new WorkerLaunchSpec(
                worker,
                $"--preview \"{inputPath}\"",
                Path.GetDirectoryName(worker)!,
                request.ExecutionTimeout,
                request.ExitProofTimeout,
                request.ExitProofTimeout,
                4096,
                16384);

            var supervised=await _supervisor.RunAsync(spec,_=>Task.CompletedTask,ct);
            if(ct.IsCancellationRequested)
                return PreviewScheduleResult.Cancelled(request);
            if(supervised.StopKind==WorkerStopKind.ExecutionTimeout)
                return PreviewScheduleResult.Timeout(request);
            if(!supervised.ExitProven)
                return PreviewScheduleResult.Failed(request,"preview_exit_unproven",Safe(supervised.Error));
            if(supervised.StopKind!=WorkerStopKind.Exited||supervised.ExitCode!=0)
                return PreviewScheduleResult.Failed(
                    request,
                    PreviewFailureCode(supervised.StopKind),
                    Safe(string.IsNullOrWhiteSpace(supervised.StandardError)?supervised.Error:supervised.StandardError));
            if(!File.Exists(outputPath))
                return PreviewScheduleResult.Failed(request,"preview_output_missing");

            var info=new FileInfo(outputPath);
            if(info.Length<8||info.Length>limits.MaxOutputBytes)
                return PreviewScheduleResult.Failed(request,"preview_output_size_exceeded");

            var bytes=await File.ReadAllBytesAsync(outputPath,ct);
            if(bytes.Length>limits.MaxOutputBytes)
                return PreviewScheduleResult.Failed(request,"preview_output_size_exceeded");

            PreviewWorkerResult meta;
            try
            {
                meta=JsonSerializer.Deserialize<PreviewWorkerResult>(supervised.StandardOutput,AgentOptions.JsonOptions())
                    ??throw new InvalidDataException("Preview worker metadata خالی است.");
            }
            catch(Exception e)
            {
                return PreviewScheduleResult.Failed(request,"preview_metadata_invalid",Safe(e.Message));
            }

            if(!meta.Success||meta.Width!=metrics.WidthPixels||meta.Height<1||
               meta.Height>limits.MaxHeightPixels||
               (long)meta.Width*meta.Height>limits.MaxPixelArea||
               meta.DpiX!=request.DpiX||meta.DpiY!=request.DpiY)
                return PreviewScheduleResult.Failed(request,"preview_metadata_mismatch");

            var actualHash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if(!string.Equals(actualHash,meta.PngSha256,StringComparison.OrdinalIgnoreCase))
                return PreviewScheduleResult.Failed(request,"preview_hash_mismatch");

            _log.Info("render_completed",$"preview session={Short(request.SessionId)}; revision={request.Revision}; width={meta.Width}; height={meta.Height}");
            return PreviewScheduleResult.Completed(
                request,
                new PreviewRenderData(
                    bytes,
                    meta.Width,
                    meta.Height,
                    meta.DpiX,
                    meta.DpiY,
                    actualHash,
                    meta.RendererVersion,
                    meta.FontFamily,
                    meta.BundledFont));
        }
        finally
        {
            await DeleteEvidenceAsync(inputPath);
            await DeleteEvidenceAsync(outputPath);
        }
    }

    private async Task DeleteEvidenceAsync(string path)
    {
        for(var attempt=0;attempt<4;attempt++)
        {
            try
            {
                if(!File.Exists(path))return;
                File.Delete(path);
                if(!File.Exists(path))return;
            }
            catch when(attempt<3){}
            if(attempt<3)await Task.Delay(25*(attempt+1));
        }
        try{_log.Info("preview_cleanup_deferred",$"file={Path.GetFileName(path)}");}catch{}
    }

    private static string PreviewFailureCode(WorkerStopKind kind)=>kind switch
    {
        WorkerStopKind.LaunchFailed=>"preview_worker_launch_failed",
        WorkerStopKind.GuardFailed=>"preview_worker_guard_failed",
        WorkerStopKind.StartSignalFailed=>"preview_worker_guard_ready_failed",
        WorkerStopKind.ServiceShutdown=>"preview_cancelled",
        WorkerStopKind.ExecutionTimeout=>"preview_timeout",
        WorkerStopKind.ExitUnproven=>"preview_exit_unproven",
        _=>"preview_worker_failed"
    };

    private static string Short(string value)=>value.Length<=12?value:value[..12];
    private static string Safe(string? value)=>SafeLogText.Sanitize(value,300);

    private sealed record PreviewWorkerInput(
        string PayloadJson,
        double PaperWidthMm,
        double PrintableWidthMm,
        int DpiX,
        int DpiY,
        string OutputPath,
        int MaxPayloadBytes,
        int MaxTextCharacters,
        int MaxItems,
        int MaxHeightPixels,
        long MaxPixelArea,
        int MaxOutputBytes);

    private sealed record PreviewWorkerResult(
        bool Success,
        int Width,
        int Height,
        int DpiX,
        int DpiY,
        string PngSha256,
        string RendererVersion,
        string FontFamily,
        bool BundledFont);
}
