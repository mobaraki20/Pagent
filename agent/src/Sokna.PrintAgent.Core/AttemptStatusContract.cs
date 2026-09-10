using System.Globalization;

namespace Sokna.PrintAgent.Core;

public static class ApiTimestampPolicy
{
    public static bool TryParseExplicitOffset(string? value,out DateTimeOffset parsed)
    {
        parsed=default;
        if(string.IsNullOrWhiteSpace(value))return false;
        var text=value.Trim();
        var t=text.IndexOf('T');
        if(t<0)return false;
        var hasZulu=text.EndsWith('Z',StringComparison.OrdinalIgnoreCase);
        var plus=text.LastIndexOf('+');
        var minus=text.LastIndexOf('-');
        var offsetIndex=Math.Max(plus,minus);
        if(!hasZulu&&offsetIndex<=t)return false;
        return DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out parsed);
    }

    public static DateTimeOffset ParseRequired(string? value,string fieldName)
    {
        if(!TryParseExplicitOffset(value,out var parsed))
            throw new PrintProtocolException("timestamp","timestamp_offset_required",$"{fieldName} باید timestamp معتبر با offset صریح باشد.");
        return parsed;
    }
}

public static class AttemptStatusContract
{
    private static readonly HashSet<string> KnownStates=new(StringComparer.Ordinal)
    {
        "reserved","claimed","started","submitted","failed","expired","cancelled","unknown","recovery_hold"
    };

    public static void ValidateForJob(AttemptStatusResult result,LocalJob job)
    {
        if(!result.Success)
            throw Fault("attempt_status_business_failed","Attempt status success=false است.");
        if(result.AttemptId!=job.AttemptId)
            throw Fault("attempt_status_attempt_mismatch","Attempt status attempt_id با تلاش محلی تطابق ندارد.");
        if(result.JobId!=job.ServerJobId)
            throw Fault("attempt_status_job_mismatch","Attempt status job_id با کار محلی تطابق ندارد.");
        if(!result.ReceiptMatches)
            throw Fault("attempt_status_receipt_mismatch","Attempt status local receipt را تأیید نکرد.");
        if(result.RequiresHumanResolution)
            throw Fault("attempt_status_human_resolution","Attempt status نیازمند تعیین تکلیف انسانی است.");
        if(string.IsNullOrWhiteSpace(result.AttemptState)||!KnownStates.Contains(result.AttemptState))
            throw Fault("attempt_status_unknown_state","Attempt status state ناشناخته/خالی است.");
        if(string.IsNullOrWhiteSpace(result.JobState))
            throw Fault("attempt_status_job_state_missing","Attempt status job_state خالی است.");
        if(string.IsNullOrWhiteSpace(result.NextAction))
            throw Fault("attempt_status_next_action_missing","Attempt status next_action خالی است.");

        _=ApiTimestampPolicy.ParseRequired(result.ServerTime,"server_time");
        if(!string.IsNullOrWhiteSpace(result.LeaseExpiresAt))
            _=ApiTimestampPolicy.ParseRequired(result.LeaseExpiresAt,"lease_expires_at");

        var state=result.AttemptState;
        var action=result.NextAction;
        if(state=="claimed"&&action is not ("start" or "continue"))
            throw Fault("attempt_status_next_action_mismatch","Attempt claimed با next_action ناسازگار است.");
        if(state=="started"&&action is not ("continue" or "report" or "start"))
            throw Fault("attempt_status_next_action_mismatch","Attempt started با next_action ناسازگار است.");
        if(result.Terminal&&action=="continue")
            throw Fault("attempt_status_terminal_continue","Attempt terminal اجازهٔ continue ندارد.");
        if(state is "expired" or "cancelled" or "failed" && !result.Terminal)
            throw Fault("attempt_status_terminal_flag_mismatch","Attempt terminal state باید terminal=true داشته باشد.");
        if(state is "claimed" or "started" && result.Terminal)
            throw Fault("attempt_status_terminal_flag_mismatch","Attempt فعال نباید terminal=true باشد.");
    }

    private static PrintProtocolException Fault(string code,string message)
        =>new("attempt_status",code,message);
}
