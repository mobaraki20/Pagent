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
            RedirectStandardOutput=false
        };
        var process=Process.Start(psi)??throw new InvalidOperationException("PrintWorker اجرا نشد.");
        return new SystemWorkerProcess(process,spec.StandardErrorLimit);
    }

    private sealed class SystemWorkerProcess : IWorkerProcess
    {
        private readonly Process _process;
        private readonly int _stderrLimit;
        private readonly StringBuilder _stderr=new();
        private readonly object _stderrLock=new();
        private WorkerProcessGuard? _guard;

        public SystemWorkerProcess(Process process,int stderrLimit)
        {
            _process=process;
            _stderrLimit=Math.Max(64,stderrLimit);
            _process.ErrorDataReceived+=OnErrorData;
            _process.BeginErrorReadLine();
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

        public ValueTask DisposeAsync()
        {
            try{_process.CancelErrorRead();}catch{}
            _guard?.Dispose();
            _process.Dispose();
            return ValueTask.CompletedTask;
        }

        private void OnErrorData(object sender,DataReceivedEventArgs args)
        {
            if(string.IsNullOrEmpty(args.Data))return;
            lock(_stderrLock)
            {
                if(_stderr.Length>=_stderrLimit)return;
                var remaining=_stderrLimit-_stderr.Length;
                var text=args.Data.Length<=remaining?args.Data:args.Data[..remaining];
                _stderr.Append(text);
                if(_stderr.Length<_stderrLimit)_stderr.AppendLine();
            }
        }
    }
}
