using DeckApi.Decks;

namespace DeckApi;

/// <summary>Error bodies shaped like the old API Platform JSON errors (<c>title</c>, <c>detail</c>, <c>status</c>, <c>violations</c>).</summary>
public static class Problems
{
    public static IResult Validation(IReadOnlyList<InputViolation> violations) => Results.Json(new
    {
        type = "/validation_errors",
        title = "An error occurred",
        detail = string.Join("\n", violations.Select(v => $"{v.PropertyPath}: {v.Message}")),
        status = 422,
        violations = violations.Select(v => new { propertyPath = v.PropertyPath, message = v.Message, code = (string?)null }),
    }, statusCode: 422);

    public static IResult NotFound() => Error(404, "Not Found");
    public static IResult Unauthorized() => Error(401, "Full authentication is required to access this resource.");
    public static IResult Forbidden() => Error(403, "Access Denied.");
    public static IResult BadRequest(string detail) => Error(400, detail);

    public static IResult Error(int status, string detail) =>
        Results.Json(new { title = "An error occurred", detail, status }, statusCode: status);
}
