namespace Dignite.DocumentAI.Documents.Figures;

/// <summary>
/// Routing lifecycle of a <see cref="DocumentFigure"/> candidate (#306). Sub-document routing uses this as a
/// durable, resumable work-queue marker: it processes <see cref="Pending"/> candidates, so a crash mid-routing
/// resumes only the unfinished ones without duplicate-spawning or re-paying the gate classification.
/// </summary>
public enum DocumentFigureStatus
{
    /// <summary>Not yet evaluated by sub-document routing (the initial state).</summary>
    Pending = 0,

    /// <summary>Routed to a derived <c>Document</c> (recorded in <see cref="DocumentFigure.RoutedDocumentId"/>).</summary>
    Spawned = 1,

    /// <summary>The routing gate judged the figure not itself a document; its candidate crop blob is deleted.</summary>
    NotADocument = 2
}
