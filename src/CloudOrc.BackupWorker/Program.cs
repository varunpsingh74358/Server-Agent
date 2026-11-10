using System.Text;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.BackupWorker;
using CloudOrc.BackupWorker.Orchestration;

// CloudOrc.BackupWorker.exe --job-id <id>
// Never started by hand: Agent Core launches it per job, places it in the Resource Governor's
// Job Object, then writes the "start" envelope (spec + grant) to stdin. Exit code 0 = a result
// message was sent (which may itself be FAILED); non-zero = protocol/start failure.

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

var stdin = Console.In;
var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };

WorkerJobSpec? spec;
try
{
    spec = await WorkerChannel.ReadStartAsync(stdin).ConfigureAwait(false);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Invalid start message: {ex.GetType().Name}");
    return 2;
}

if (spec is null)
{
    Console.Error.WriteLine("No start message received.");
    return 2;
}

var channel = new WorkerChannel(stdin, stdout, spec.Grant);
_ = Task.Run(channel.ListenAsync);

WorkerResult result;
try
{
    result = await JobExecutor.RunAsync(spec, channel, channel.Cancellation.Token).ConfigureAwait(false);
}
catch (OperationCanceledException) when (channel.Cancellation.IsCancellationRequested)
{
    result = new WorkerResult { State = BackupJobStates.Cancelled, ReasonCode = BackupReasonCodes.Cancelled, Message = "Cancelled." };
}
catch (Exception ex)
{
    result = new WorkerResult
    {
        State = BackupJobStates.Failed,
        ReasonCode = BackupReasonCodes.EngineFailed,
        Message = $"Worker error: {ex.GetType().Name}: {ex.Message}"
    };
}

channel.Result(result);
return 0;
