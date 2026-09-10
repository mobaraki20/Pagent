using System.Diagnostics;
using System.Text;

namespace Sokna.PrintAgent.Service;

public sealed class SystemWorkerProcessFactory : IWorkerProcessFactory
{
    public IWorkerProcess Start(WorkerLaunchSpec spec)
    {
        if(string.IsNullOrWhiteSpace(spec.FileName))throw new ArgumentException("Worker executable path is required.",nameof(spec));
        var psi=new ProcessStartInfo(spec.FileName,spec.Arguments)
        {
            UseShellExecute=false,
            CreateNoWindow=true,
            WorkingDirectory=spec.WorkingDirectory,
            RedirectStandardError=true,
            RedirectStandardOutput=true
        };
        var process=Process.Start(psi)??throw new InvalidOperationException("PrintWorker اجرا نشد.");
        return new SystemWorkerProcess(process,spec.StandardErrorLimit,spec.StandardOutputLimit);
    }

    private sealed class SystemWorkerProcess : IWorkerProcess
    {
        private readonly Process _process;
        private readonly int _stderrLimit;
        private readonly int _stdoutLimit;
        private readonly StringBuilder _stderr=new();
        private readonly StringBuilder _stdout=new();
        private readonly object _stderrLock=new();
        private readonly object _stdoutLock=new();
        private WorkerProcessGuard? _guard;

        public SystemWorkerProcess(Process process,int stderrLimit,int stdoutLimit)
        {
            _process=process;
            _stderrLimit=Math.Max(64,stderrLimit);
            _stdoutLimit=Math.Max(64,stdoutLimit);
            _process.ErrorDataReceived+=OnErrorData;
            _process.OutputDataReceived+=OnOutputData;
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();
        }

        public bool HasExited
        {
            get
            {
                try{return _process.HasExited;}
                catch{return false;}
            }
        }

        public int? ExitCode
        {
            get
            {
                try{return _process.HasExited?_process.ExitCode:null;}
                catch{return null;}
            }
        }

        public void AttachGuard()
        {
            if(_guard is not null)return;
            _guard=WorkerProcessGuard.Attach(_process);
        }

        public void KillTree()
        {
            if(!_process.HasExited)_process.Kill(true);
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)=>_process.WaitForExitAsync(cancellationToken);

        public string GetBoundedStandardError()
        {
            lock(_stderrLock)return _stderr.ToString();
        }

        public string GetBoundedStandardOutput()
        {
            lock(_stdoutLock)return _stdout.ToString();
        }

        public ValueTask DisposeAsync()
        {
            try{_process.CancelErrorRead();}catch{}
            try{_process.CancelOutputRead();}catch{}
            _guard?.Dispose();
            _process.Dispose();
            return ValueTask.CompletedTask;
        }

        private void OnErrorData(object sender,DataReceivedEventArgs args)=>AppendBounded(_stderr,_stderrLock,_stderrLimit,args.Data);
        private void OnOutputData(object sender,DataReceivedEventArgs args)=>AppendBounded(_stdout,_stdoutLock,_stdoutLimit,args.Data);

        private static void AppendBounded(StringBuilder target,object gate,int limit,string? value)
        {
            if(string.IsNullOrEmpty(value))return;
            lock(gate)
            {
                if(target.Length>=limit)return;
                var remaining=limit-target.Length;
                var text=value.Length<=remaining?value:value[..remaining];
                target.Append(text);
                if(target.Length<limit)target.AppendLine();
            }
        }
    }
}
