import { createContext, useCallback, useContext, useState, type ReactNode } from "react";
import { Icon } from "@/shared/icons";

type ToastTone = "success" | "warning" | "error" | "info";
interface ToastItem { id: number; tone: ToastTone; text: string }

interface ToastApi {
  push: (text: string, tone?: ToastTone) => void;
  success: (text: string) => void;
  error: (text: string) => void;
}

const Ctx = createContext<ToastApi | null>(null);
let seq = 0;

const ICON: Record<ToastTone, ReactNode> = {
  success: <Icon.Check size={16} />,
  warning: <Icon.Alert size={16} />,
  error: <Icon.Alert size={16} />,
  info: <Icon.Info size={16} />,
};

export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<ToastItem[]>([]);

  const remove = useCallback((id: number) => setItems((xs) => xs.filter((x) => x.id !== id)), []);
  const push = useCallback((text: string, tone: ToastTone = "info") => {
    const id = ++seq;
    setItems((xs) => [...xs, { id, tone, text }]);
    window.setTimeout(() => remove(id), tone === "error" ? 6000 : 3500);
  }, [remove]);

  const api: ToastApi = {
    push,
    success: (t) => push(t, "success"),
    error: (t) => push(t, "error"),
  };

  return (
    <Ctx.Provider value={api}>
      {children}
      <div className="nx-toasts" aria-live="polite" role="status">
        {items.map((it) => (
          <div key={it.id} className={`nx-toast nx-toast--${it.tone}`}>
            <span style={{ color: `var(--nx-${it.tone})`, flexShrink: 0, marginTop: 1 }}>{ICON[it.tone]}</span>
            <span style={{ flex: 1 }}>{it.text}</span>
            <button className="nx-iconbtn" style={{ width: 22, height: 22 }} aria-label="Fermer" onClick={() => remove(it.id)}>
              <Icon.X size={13} />
            </button>
          </div>
        ))}
      </div>
    </Ctx.Provider>
  );
}

export function useToast(): ToastApi {
  const ctx = useContext(Ctx);
  if (!ctx) throw new Error("useToast doit être utilisé dans <ToastProvider>");
  return ctx;
}
