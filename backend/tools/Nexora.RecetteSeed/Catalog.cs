namespace Nexora.RecetteSeed;

/// <summary>Entreprise de recette et ses identifiants de référentiel (posés par la démo ou le seed SQL).</summary>
internal sealed record Co(
    string Key, Guid Id, string Name, Guid Location, Guid Reserve,
    Guid Cash, Guid Bank, Guid Momo, Guid Register, Guid CashMethod, Guid CardMethod,
    Guid DefaultUnit, Guid TaxExempt, Guid TaxVat, string Prefix, bool BankInUsd = false)
{
    /// <summary>Compte qui reçoit les cartes en gourdes : la banque si elle est en HTG, sinon aucun (pas de carte).</summary>
    public Guid? HtgCardAccount => BankInUsd ? null : Bank;

    public static readonly Co Pharmacy = new(
        "pharma", G("0000-0000000000c1"), "Pharmacie ABC",
        Location: G("0000-00000000a003"), Reserve: G("0000-00000000a004"),
        Cash: G("0000-00000000a051"), Bank: G("0000-00000000a052"), Momo: G("0000-00000000a053"),
        Register: G("0000-00000000a061"), CashMethod: G("0000-00000000a081"), CardMethod: G("0000-00000000a082"),
        DefaultUnit: G("0000-00000000a011"), TaxExempt: G("0000-00000000a021"), TaxVat: G("0000-00000000a022"),
        Prefix: "PH", BankInUsd: true);   // Compte Unibank en USD

    public static readonly Co Supermarket = new(
        "super", G("0000-0000000000c3"), "Supermarché ABC",
        Location: G("0003-000000000006"), Reserve: G("0003-000000000007"),
        Cash: G("0003-000000000051"), Bank: G("0003-000000000052"), Momo: G("0003-000000000053"),
        Register: G("0003-000000000061"), CashMethod: G("0003-000000000081"), CardMethod: G("0003-000000000082"),
        DefaultUnit: G("0003-000000000011"), TaxExempt: G("0003-000000000021"), TaxVat: G("0003-000000000022"),
        Prefix: "SM");

    public static Guid G(string tail) => Guid.Parse($"dce00000-0000-0000-{tail}");
}

/// <summary>
/// Rôle d'un produit dans les scénarios de recette. Chaque profil provoque volontairement un
/// signal précis (alerte, recommandation, anomalie) — voir docs/phase-55-jeu-de-donnees-recette.md.
/// </summary>
internal enum Profile
{
    Regular,       // rotation normale
    FastMover,     // forte rotation — réassort fréquent
    LowStock,      // stock final sous le point de commande → alerte + recommandation de réassort
    Overstock,     // stock très supérieur à la demande → surstock / dormant
    Dormant,       // en stock, aucune vente depuis 90 jours
    PriceIncrease, // coût fournisseur en hausse → recommandation « hausse de prix fournisseur »
    NoSupplier,    // aucun fournisseur référencé (§29)
    Weighed,       // vendu au poids, quantités à 3 décimales, code PLU
    Floor,         // prix plancher (min_price) — refus sous le plancher
    NearExpiry,    // lot proche de l'expiration (pharmacie) → alerte péremption
    ExpiredLot,    // lot déjà expiré en stock → balayage / retrait
    Prescription,  // médicament sur ordonnance
}

internal sealed record ProductSpec(
    string Sku, string Name, string Category, decimal Price, decimal Cost, double DailyDemand,
    Profile Profile = Profile.Regular, bool Vat = false, int Supplier = 0, int LeadTimeDays = 5,
    bool Lots = false, string? Plu = null, bool Kg = false, decimal? MinPrice = null, decimal? UsdPrice = null);

internal static class Catalog
{
    public static readonly string[] SuperCategories =
        ["Épicerie", "Boissons", "Produits frais", "Hygiène & entretien", "Bébé", "Boulangerie", "Fruits & légumes"];

    /// <summary>32 produits (§5 : 30 minimum), rotations et marges variées.</summary>
    public static readonly ProductSpec[] Supermarket =
    [
        new("SM-RIZ5",   "Riz long grain 5 kg",          "Épicerie",   1150m,  890m, 6.0, Profile.FastMover, Supplier: 0, LeadTimeDays: 7),
        new("SM-RIZ1",   "Riz long grain 1 kg",          "Épicerie",    250m,  185m, 5.0, Profile.FastMover, Supplier: 0),
        new("SM-HUI1",   "Huile végétale 1 L",           "Épicerie",    425m,  300m, 4.0, Profile.PriceIncrease, Supplier: 1, LeadTimeDays: 10),
        new("SM-SUC1",   "Sucre blanc 1 kg",             "Épicerie",    175m,  130m, 4.5, Supplier: 0),
        new("SM-FAR1",   "Farine de blé 1 kg",           "Épicerie",    150m,  105m, 2.5, Supplier: 0),
        new("SM-PAT500", "Pâtes spaghetti 500 g",        "Épicerie",    110m,   70m, 3.0, Supplier: 1),
        new("SM-HAR",    "Haricots rouges 1 kg",         "Épicerie",    260m,  190m, 2.0, Profile.LowStock, Supplier: 0, LeadTimeDays: 12),
        new("SM-CAF250", "Café moulu 250 g",             "Épicerie",    390m,  240m, 1.2, Vat: true, Supplier: 1),
        new("SM-LAI400", "Lait en poudre 400 g",         "Épicerie",    720m,  560m, 1.5, Vat: true, Supplier: 2, MinPrice: 690m),
        new("SM-SAR",    "Sardines à l'huile 125 g",     "Épicerie",    140m,   85m, 1.8, Supplier: 2),
        new("SM-KET",    "Ketchup 500 g",                "Épicerie",    310m,  170m, 0.0, Profile.Dormant, Vat: true, Supplier: 2),
        new("SM-EAU15",  "Eau minérale 1,5 L",           "Boissons",     75m,   40m, 9.0, Profile.FastMover, Supplier: 3),
        new("SM-SOD33",  "Soda cola 33 cl",              "Boissons",     90m,   50m, 6.0, Vat: true, Supplier: 3),
        new("SM-JUS1",   "Jus d'orange 1 L",             "Boissons",    260m,  175m, 1.5, Vat: true, Supplier: 3),
        new("SM-BIE",    "Bière locale 65 cl",           "Boissons",    175m,  115m, 2.5, Vat: true, Supplier: 3),
        new("SM-RHU",    "Rhum vieux 75 cl",             "Boissons",   1850m, 1100m, 0.4, Vat: true, Supplier: 4, MinPrice: 1700m),
        new("SM-VIN",    "Vin rouge importé 75 cl",      "Boissons",   2400m, 1600m, 0.05, Profile.Overstock, Vat: true, Supplier: 4),
        new("SM-OEU30",  "Œufs — plateau de 30",         "Produits frais", 650m, 480m, 2.0, Supplier: 2, LeadTimeDays: 2),
        new("SM-FROM",   "Fromage à tartiner 200 g",     "Produits frais", 330m, 220m, 0.8, Vat: true, Supplier: 2),
        new("SM-YAO",    "Yaourt nature x4",             "Produits frais", 280m, 190m, 1.0, Supplier: 2, LeadTimeDays: 2),
        new("SM-SAV",    "Savon de toilette 100 g",      "Hygiène & entretien", 65m, 38m, 3.0, Vat: true, Supplier: 1),
        new("SM-DET1",   "Détergent poudre 1 kg",        "Hygiène & entretien", 350m, 230m, 1.5, Vat: true, Supplier: 1),
        new("SM-JAV",    "Eau de javel 1 L",             "Hygiène & entretien", 120m, 70m, 1.2, Vat: true, Supplier: 1),
        new("SM-PQ4",    "Papier toilette x4",           "Hygiène & entretien", 210m, 150m, 2.0, Vat: true, Supplier: 1),
        new("SM-DENT",   "Dentifrice 100 ml",            "Hygiène & entretien", 185m, 110m, 1.0, Vat: true, Supplier: 4),
        new("SM-COU",    "Couches bébé taille 3 x30",    "Bébé",       1450m, 1050m, 0.8, Vat: true, Supplier: 4),
        new("SM-LAIB",   "Lait infantile 1er âge 400 g", "Bébé",       1250m,  980m, 0.5, Supplier: 4, MinPrice: 1200m),
        new("SM-PAIN",   "Pain de mie 500 g",            "Boulangerie", 175m,  110m, 3.5, Profile.NoSupplier),
        new("SM-BAN",    "Bananes (au kg)",              "Fruits & légumes", 120m, 70m, 4.0, Profile.Weighed, Supplier: 2, Plu: "4011", Kg: true),
        new("SM-TOM",    "Tomates (au kg)",              "Fruits & légumes", 180m, 110m, 2.5, Profile.Weighed, Supplier: 2, Plu: "4087", Kg: true),
        new("SM-OIG",    "Oignons (au kg)",              "Fruits & légumes", 160m, 95m, 2.0, Profile.Weighed, Supplier: 2, Plu: "4093", Kg: true),
        new("SM-AVO",    "Avocats (au kg)",              "Fruits & légumes", 300m, 170m, 0.9, Profile.Weighed, Supplier: 2, Plu: "4046", Kg: true),
    ];

    public static readonly (string Name, string Email, string? Tax)[] SuperSuppliers =
    [
        ("Grossiste Caraïbes S.A.", "commandes@caraibes.demo", "NIF-001-458-221"),
        ("Distribution Import Plus", "achats@importplus.demo", "NIF-002-117-903"),
        ("Laiterie & Frais du Nord", "frais@nord.demo", null),
        ("Brasserie Nationale", "b2b@brasserie.demo", "NIF-004-330-110"),
        ("Hygiène Pro Distribution", "pro@hygiene.demo", null),
    ];

    /// <summary>Pharmacie : 14 produits en plus des 2 de la démo, dont lots et ordonnances.</summary>
    public static readonly string[] PharmaCategories = ["Vitamines", "Dermatologie", "Hygiène bucco-dentaire", "Premiers soins", "Antipaludéens"];

    public static readonly ProductSpec[] Pharmacy =
    [
        new("PH-IBU400", "Ibuprofène 400 mg — 20 cp",   "Antalgiques",   120m,  70m, 3.0, Profile.FastMover, Lots: true, Supplier: 0),
        new("PH-ASP",    "Aspirine 500 mg — 20 cp",     "Antalgiques",    95m,  55m, 1.5, Lots: true, Supplier: 0),
        new("PH-AUG",    "Amox./ac. clavulanique 1 g",  "Antibiotiques", 480m, 320m, 0.8, Profile.Prescription, Lots: true, Supplier: 1),
        new("PH-AZI",    "Azithromycine 500 mg — 3 cp", "Antibiotiques", 390m, 250m, 0.6, Profile.Prescription, Lots: true, Supplier: 1),
        new("PH-ART",    "Artéméther/luméfantrine",     "Antipaludéens", 520m, 360m, 1.2, Profile.NearExpiry, Lots: true, Supplier: 1),
        new("PH-VITC",   "Vitamine C 1000 mg — 20 cp",  "Vitamines",     260m, 150m, 1.5, Lots: true, Supplier: 0, UsdPrice: 2.10m),
        new("PH-MULTI",  "Multivitamines — 30 gél.",    "Vitamines",     650m, 420m, 0.6, Lots: true, Supplier: 0),
        new("PH-SIR",    "Sirop toux adulte 125 ml",    "Premiers soins", 310m, 190m, 0.0, Profile.ExpiredLot, Lots: true, Supplier: 0),
        new("PH-PANS",   "Pansements assortis x20",     "Premiers soins", 150m,  80m, 1.0, Vat: true, Supplier: 0),
        new("PH-ALC",    "Alcool à 70° 250 ml",         "Premiers soins", 130m,  70m, 1.2, Vat: true, Supplier: 0),
        new("PH-CREM",   "Crème hydratante 200 ml",     "Dermatologie",  890m, 520m, 0.3, Profile.Overstock, Vat: true, Supplier: 1),
        new("PH-SOL",    "Écran solaire SPF50",         "Dermatologie", 1250m, 780m, 0.0, Profile.Dormant, Vat: true, Supplier: 1),
        new("PH-BROS",   "Brosse à dents souple",       "Hygiène bucco-dentaire", 110m, 55m, 1.0, Vat: true, Supplier: 0, MinPrice: 100m),
        new("PH-BAIN",   "Bain de bouche 250 ml",       "Hygiène bucco-dentaire", 340m, 210m, 0.5, Profile.LowStock, Vat: true, Supplier: 1, LeadTimeDays: 14),
    ];
}
