import { useEffect, useRef, useState } from "react";
import { Button } from "@/shared/ui";
import type { PendingImage } from "@/shared/syncOperations";

// Capture des preuves terrain (finalisation phase 12) : photo compressée, signature au doigt, position GPS facultative.

/** Taille maximale acceptée par le serveur pour une pièce jointe terrain. */
export const MAX_PROOF_BYTES = 5 * 1024 * 1024;
export const base64Bytes = (b64: string) => Math.floor((b64.length * 3) / 4);

/** Réduit une photo (≤ 1280 px, JPEG) : une photo de téléphone dépasse souvent la limite de 5 Mo. */
export async function compressPhoto(file: File, maxSide = 1280, quality = 0.72): Promise<PendingImage> {
  const url = URL.createObjectURL(file);
  try {
    const img = await new Promise<HTMLImageElement>((resolve, reject) => {
      const i = new Image();
      i.onload = () => resolve(i);
      i.onerror = () => reject(new Error("Image illisible."));
      i.src = url;
    });
    const scale = Math.min(1, maxSide / Math.max(img.width, img.height));
    const canvas = document.createElement("canvas");
    canvas.width = Math.round(img.width * scale);
    canvas.height = Math.round(img.height * scale);
    canvas.getContext("2d")!.drawImage(img, 0, 0, canvas.width, canvas.height);
    const dataUrl = canvas.toDataURL("image/jpeg", quality);
    return { base64: dataUrl.split(",")[1] ?? "", contentType: "image/jpeg" };
  } finally {
    URL.revokeObjectURL(url);
  }
}

/** Position actuelle — à n'appeler que si l'entreprise l'a prévue ; le navigateur demande l'accord de l'utilisateur. */
export function currentPosition(timeoutMs = 8000): Promise<{ latitude: number; longitude: number } | null> {
  if (typeof navigator === "undefined" || !navigator.geolocation) return Promise.resolve(null);
  return new Promise((resolve) => navigator.geolocation.getCurrentPosition(
    (p) => resolve({ latitude: Math.round(p.coords.latitude * 1e6) / 1e6, longitude: Math.round(p.coords.longitude * 1e6) / 1e6 }),
    () => resolve(null),
    { enableHighAccuracy: true, timeout: timeoutMs, maximumAge: 60_000 },
  ));
}

/** Zone de signature au doigt / à la souris. `onChange(null)` quand elle est effacée. */
export function SignaturePad({ onChange, label = "Signature du client" }: { onChange: (img: PendingImage | null) => void; label?: string }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const drawing = useRef(false);
  const [empty, setEmpty] = useState(true);

  useEffect(() => {
    const c = canvas.current!;
    const ratio = window.devicePixelRatio || 1;
    c.width = c.offsetWidth * ratio;
    c.height = c.offsetHeight * ratio;
    const ctx = c.getContext("2d")!;
    ctx.scale(ratio, ratio);
    ctx.lineWidth = 2.4;
    ctx.lineCap = "round";
    ctx.strokeStyle = "#111";
    ctx.fillStyle = "#fff";
    ctx.fillRect(0, 0, c.width, c.height);
  }, []);

  const point = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const r = e.currentTarget.getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top };
  };
  const down = (e: React.PointerEvent<HTMLCanvasElement>) => {
    e.currentTarget.setPointerCapture(e.pointerId);
    drawing.current = true;
    const ctx = e.currentTarget.getContext("2d")!;
    const p = point(e);
    ctx.beginPath();
    ctx.moveTo(p.x, p.y);
  };
  const move = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (!drawing.current) return;
    const ctx = e.currentTarget.getContext("2d")!;
    const p = point(e);
    ctx.lineTo(p.x, p.y);
    ctx.stroke();
    if (empty) setEmpty(false);
  };
  const up = () => {
    if (!drawing.current) return;
    drawing.current = false;
    if (!empty) onChange({ base64: canvas.current!.toDataURL("image/png").split(",")[1] ?? "", contentType: "image/png" });
  };
  const clear = () => {
    const c = canvas.current!;
    const ctx = c.getContext("2d")!;
    ctx.save();
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.fillStyle = "#fff";
    ctx.fillRect(0, 0, c.width, c.height);
    ctx.restore();
    setEmpty(true);
    onChange(null);
  };

  return (
    <div style={{ display: "grid", gap: 4 }}>
      <canvas ref={canvas} aria-label={label} role="img"
        onPointerDown={down} onPointerMove={move} onPointerUp={up} onPointerLeave={up}
        style={{ width: "100%", height: 160, border: "1px dashed var(--nx-border)", borderRadius: 8, background: "#fff", touchAction: "none" }} />
      <div style={{ display: "flex", justifyContent: "space-between", fontSize: 12, color: "var(--nx-text-2)" }}>
        <span>{empty ? "Faites signer dans le cadre." : "Signature recueillie."}</span>
        <Button type="button" size="sm" variant="ghost" onClick={clear} disabled={empty}>Effacer</Button>
      </div>
    </div>
  );
}

/** Prise de photo (appareil arrière sur mobile), compressée, avec aperçu. */
export function PhotoInput({ onChange }: { onChange: (img: PendingImage | null) => void }) {
  const [preview, setPreview] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  return (
    <div style={{ display: "grid", gap: 6 }}>
      <input type="file" accept="image/*" capture="environment" aria-label="Photo de la livraison"
        onChange={async (e) => {
          const f = e.target.files?.[0];
          setError(null);
          if (!f) { setPreview(null); onChange(null); return; }
          try {
            const img = await compressPhoto(f);
            if (base64Bytes(img.base64) > MAX_PROOF_BYTES) { setError("Photo trop lourde, même réduite."); onChange(null); return; }
            setPreview(`data:${img.contentType};base64,${img.base64}`);
            onChange(img);
          } catch (err) {
            setError(err instanceof Error ? err.message : "Photo illisible.");
            onChange(null);
          }
        }} />
      {error && <div className="nx-error-text">{error}</div>}
      {preview && <img src={preview} alt="Aperçu de la photo" style={{ maxWidth: "100%", maxHeight: 200, borderRadius: 8, objectFit: "contain" }} />}
    </div>
  );
}
