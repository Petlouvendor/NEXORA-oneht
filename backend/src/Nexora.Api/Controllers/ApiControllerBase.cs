using Microsoft.AspNetCore.Mvc;
using Nexora.Domain.Common;

namespace Nexora.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public abstract class ApiControllerBase : ControllerBase
{
    protected IActionResult FromResult<T>(Result<T> result) =>
        result.IsSuccess ? Ok(result.Value) : throw new DomainException(result.Error!);

    protected IActionResult FromResult(Result result) =>
        result.IsSuccess ? NoContent() : throw new DomainException(result.Error!);

    /// <summary>Type demandé par l'application web pour recevoir un fichier dans une enveloppe JSON.</summary>
    public const string FileEnvelopeMediaType = "application/vnd.nexora.file+json";

    /// <summary>
    /// Fichier téléchargé (reçu PDF, classeur Excel, CSV). Par défaut : le fichier lui-même. L'application
    /// web demande <see cref="FileEnvelopeMediaType"/> et reçoit le fichier en base64 dans du JSON : un
    /// navigateur réglé pour « télécharger les PDF » ou un gestionnaire de téléchargements intercepte sinon
    /// la réponse PDF, et la page n'en reçoit qu'un corps vide (fichier de 0 octet impossible à ouvrir).
    /// </summary>
    protected IActionResult Download(byte[] content, string contentType, string fileName)
    {
        var envelope = Request.Headers.Accept.Any(a => a is not null && a.Contains(FileEnvelopeMediaType, StringComparison.OrdinalIgnoreCase));
        return envelope
            ? Ok(new FileEnvelope(fileName, contentType, content.Length, Convert.ToBase64String(content)))
            : File(content, contentType, fileName);
    }
}

/// <summary>Fichier transporté dans du JSON (voir <see cref="ApiControllerBase.Download"/>).</summary>
public sealed record FileEnvelope(string FileName, string ContentType, int Size, string Base64);
