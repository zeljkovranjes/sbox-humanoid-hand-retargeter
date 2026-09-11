#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HumanoidHandRetargeter.Editor;

public static partial class HandRetargetApi
{
    static readonly object Gate=new();
    static readonly SemaphoreSlim Queue=new(1,1);
    static readonly Dictionary<string,Job> Jobs=new();
    sealed class Job
    {
        public string Id=Guid.NewGuid().ToString("N"),Status="queued",Stage="queued";
        public DateTime Created=DateTime.UtcNow;
        public object? Result,Error;
        public CancellationTokenSource Cancellation=new();
        public bool Finished=>Status is "succeeded" or "failed" or "cancelled";
        public object Snapshot()=>new{version=1,jobId=Id,status=Status,stage=Stage,createdUtc=Created,result=Result,error=Error,cancellationRequested=Cancellation.IsCancellationRequested};
    }
    public static string Submit(string requestJson)
    {
        Request request;
        try{request=Parse(requestJson);}
        catch(Exception ex) when(ex is JsonException or ArgumentException){return Serialize(new{version=1,error=new{code="invalid_request",message=ex.Message}});}
        Job job;
        lock(Gate)
        {
            if(Jobs.Values.Count(j=>!j.Finished)>=8)return Serialize(new{version=1,error=new{code="queue_full",message="At most eight jobs may be pending."}});
            foreach(var old in Jobs.Values.Where(j=>j.Finished).OrderBy(j=>j.Created).Take(Math.Max(0,Jobs.Count-63)).ToArray())
            {Jobs.Remove(old.Id);old.Cancellation.Dispose();}
            job=new();Jobs.Add(job.Id,job);
        }
        _=Process(job,request);
        lock(Gate)return Serialize(job.Snapshot());
    }
    public static string GetJob(string jobId)
    {
        lock(Gate)return Jobs.TryGetValue(jobId??"",out var job)?Serialize(job.Snapshot()):Unknown();
    }
    public static string ListJobs(){lock(Gate)return Serialize(new{version=1,jobs=Jobs.Values.Select(j=>j.Snapshot()).ToArray()});}
    public static string Cancel(string jobId)
    {
        lock(Gate)
        {
            if(!Jobs.TryGetValue(jobId??"",out var job))return Unknown();
            if(!job.Finished)job.Cancellation.Cancel();
            return Serialize(job.Snapshot());
        }
    }
    static string Unknown()=>Serialize(new{version=1,error=new{code="job_not_found",message="Unknown job ID, or jobs were cleared by editor reload."}});
    static async Task Process(Job job,Request request)
    {
        await Task.Yield();var entered=false;
        try
        {
            await Queue.WaitAsync(job.Cancellation.Token);entered=true;
            lock(Gate)job.Status="running";
            var result=await Run(request,job.Cancellation.Token,stage=>{lock(Gate)job.Stage=stage;});
            // If export committed before cancellation arrived, report its successful result.
            lock(Gate){job.Result=result;job.Status="succeeded";job.Stage="complete";}
        }
        catch(OperationCanceledException){lock(Gate){job.Status="cancelled";job.Stage="cancelled";}}
        catch(Exception ex)
        {
            lock(Gate)
            {
                job.Error=new{code=ex is MappingRequiredException?"mapping_required":ex is ArgumentException?"invalid_request":"operation_failed",
                    message=ex.Message,details=(ex as MappingRequiredException)?.Details};
                job.Status="failed";
            }
        }
        finally{if(entered)Queue.Release();}
    }
}
