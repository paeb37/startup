namespace Dexter.WebApi.Decks;

using System;
using System.Threading;
using Dexter.WebApi.Decks.Models;
using Dexter.WebApi.Decks.Services;
using Microsoft.AspNetCore.Http;
using static Dexter.WebApi.Program;

internal static class DeckEndpoints
{
    internal static void MapDeckEndpoints(WebApplication app)
    {
        app.MapPost("/api/upload", async (HttpRequest req, DeckWorkflowService workflow, CancellationToken cancellationToken)
            => await workflow.HandleUploadAsync(req, cancellationToken));

        app.MapGet("/api/decks", async (HttpRequest request, DeckWorkflowService workflow, CancellationToken cancellationToken)
            => await workflow.GetDecksAsync(request, cancellationToken));

        app.MapPost("/api/decks/{deckId:guid}/preview", async (Guid deckId, int slide, DeckWorkflowService workflow, CancellationToken cancellationToken)
            => await workflow.PreviewDeckAsync(deckId, slide, cancellationToken));

        app.MapPost("/api/decks/{deckId:guid}/redact", async (Guid deckId, DeckWorkflowService workflow, CancellationToken cancellationToken)
            => await workflow.RedactDeckAsync(deckId, cancellationToken));

        app.MapGet("/api/decks/{deckId:guid}/download", async (Guid deckId, HttpRequest request, DeckWorkflowService workflow, CancellationToken cancellationToken)
            => await workflow.DownloadDeckAsync(deckId, request, cancellationToken));

        app.MapGet("/api/decks/{deckId:guid}/slides/{slideNo:int}", async (Guid deckId, int slideNo, HttpRequest request, DeckWorkflowService workflow, CancellationToken cancellationToken)
            => await workflow.GetSlideAsync(deckId, slideNo, request, cancellationToken));

        app.MapPost("/api/render", async (HttpRequest req, DeckWorkflowService workflow, CancellationToken cancellationToken)
            => await workflow.RenderAsync(req, cancellationToken));
    }
}
