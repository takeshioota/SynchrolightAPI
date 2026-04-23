namespace SynchrolightAPI.Api.Models;

public record ApiResponse(bool Success, string? Message = null, string? Error = null, object? Data = null);
