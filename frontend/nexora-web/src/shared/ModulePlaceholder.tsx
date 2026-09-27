import { Card, EmptyState } from "@/shared/ui";

/** Emplacement de module dont les écrans arrivent dans une phase ultérieure (structure §7). */
export function ModulePlaceholder({ title }: { title: string }) {
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>{title}</h1>
      <Card>
        <EmptyState
          title={`Module « ${title} »`}
          hint="La structure et les données sont en place ; l'écran dédié est prévu dans une phase ultérieure."
        />
      </Card>
    </div>
  );
}
