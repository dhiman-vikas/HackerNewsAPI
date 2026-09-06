using System.Globalization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using RestfulAPIDemo.BestStories;

namespace RestfulAPIDemo.Api;

/// <summary>
/// Documents the hand-parsed <c>n</c> query parameter as a required, bounded integer.
/// The handler reads it from the raw query string, so the generator cannot infer it.
/// </summary>
internal sealed class BestStoriesOpenApiTransformer(IOptions<BestStoriesOptions> options) : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var isBestStories = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<IEndpointNameMetadata>()
            .Any(metadata => metadata.EndpointName == BestStoriesEndpoints.EndpointName);
        if (!isBestStories)
        {
            return Task.CompletedTask;
        }

        var max = options.Value.MaxStories;
        operation.Parameters ??= [];
        for (var i = operation.Parameters.Count - 1; i >= 0; i--)
        {
            if (operation.Parameters[i].Name == "n")
            {
                operation.Parameters.RemoveAt(i);
            }
        }

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "n",
            In = ParameterLocation.Query,
            Required = true,
            Description = string.Create(CultureInfo.InvariantCulture,
                $"Number of stories to return, between 1 and {max}. Fewer items are returned when fewer stories are available."),
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Format = "int32",
                Minimum = "1",
                Maximum = max.ToString(CultureInfo.InvariantCulture),
            },
        });

        return Task.CompletedTask;
    }
}
