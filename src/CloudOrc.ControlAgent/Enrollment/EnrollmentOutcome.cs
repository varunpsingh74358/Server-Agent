using CloudOrc.Agent.Contracts.Enrollment;

namespace CloudOrc.ControlAgent.Enrollment;

public sealed class EnrollmentOutcome
{
    private EnrollmentOutcome(bool isSuccess, EnrollmentResponse? response, string? error)
    {
        IsSuccess = isSuccess;
        Response = response;
        Error = error;
    }

    public bool IsSuccess { get; }

    public EnrollmentResponse? Response { get; }

    public string? Error { get; }

    public static EnrollmentOutcome Success(EnrollmentResponse response) => new(true, response, null);

    public static EnrollmentOutcome Failure(string error) => new(false, null, error);
}
