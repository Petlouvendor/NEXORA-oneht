import { Link, useSearchParams } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useAsync } from "@/shared/useAsync";
import { Card, Tabs } from "@/shared/ui";
import { getSetup } from "@/restaurant/restaurantApi";
import { FloorSection, KitchenSection, RecipesSection, SetupSection } from "@/restaurant/RestaurantSections";

/** Restaurant : salle (tables, commandes, paiement), cuisine (tickets), recettes, configuration. */
export function RestaurantPage() {
  const { hasPermission } = useAuth();
  const [params, setParams] = useSearchParams();
  const setup = useAsync(getSetup, []);
  const tabs = [
    { id: "floor", label: "Salle", show: hasPermission("restaurant.serve") },
    { id: "kitchen", label: "Cuisine", show: hasPermission("restaurant.kitchen") },
    { id: "recipes", label: "Recettes", show: hasPermission("recipes.manage") },
    { id: "setup", label: "Configuration", show: hasPermission("restaurant.manage") },
  ].filter((t) => t.show);
  const tab = tabs.some((t) => t.id === params.get("tab")) ? params.get("tab")! : tabs[0]?.id ?? "floor";
  const setTab = (t: string) => setParams((p) => { p.set("tab", t); return p; }, { replace: true });

  if (tabs.length === 0) return <Card>Le restaurant n'est pas accessible avec vos permissions.</Card>;
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Restaurant</h1>
      {setup.data && !setup.data.moduleEnabled && (
        <Card>
          Le module Restaurant n'est pas activé pour cette entreprise.
          {hasPermission("sectors.configure") ? <> Activez-le dans <Link to="/sectors">Secteurs &amp; modules</Link>.</> : " Demandez à un administrateur de l'activer."}
        </Card>
      )}
      <Tabs active={tab} onChange={setTab} tabs={tabs.map(({ id, label }) => ({ id, label }))} />
      {tab === "floor" && <FloorSection />}
      {tab === "kitchen" && <KitchenSection />}
      {tab === "recipes" && <RecipesSection />}
      {tab === "setup" && <SetupSection />}
    </div>
  );
}
