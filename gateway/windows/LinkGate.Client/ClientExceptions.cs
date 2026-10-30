namespace LinkGate.Client;

public sealed class BrokerRequestException : Exception
{
    public string Code { get; }
    public string Stage { get; }
    public bool Retryable { get; }
    internal BrokerRequestException(string code, string stage, bool retryable) : base("Broker request rejected.")
    { Code = code; Stage = stage; Retryable = retryable; }
}

public sealed class MutationOutcomeUnknownException : IOException
{
    public Guid OperationId { get; }
    internal MutationOutcomeUnknownException(Guid operationId) : base("Operation outcome unknown; explicitly query operation.status with the original operation ID.")
    { OperationId = operationId; }
}
