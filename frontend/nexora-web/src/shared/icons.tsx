import type { SVGProps } from "react";

/** Jeu d'icônes NEXORA — trait unique 1.6, 20×20, style cohérent (§31). */
type IconProps = SVGProps<SVGSVGElement> & { size?: number };

function Base({ size = 18, children, ...rest }: IconProps & { children: React.ReactNode }) {
  return (
    <svg
      width={size} height={size} viewBox="0 0 24 24" fill="none"
      stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" strokeLinejoin="round"
      aria-hidden="true" focusable="false" {...rest}
    >
      {children}
    </svg>
  );
}

export const Icon = {
  Home: (p: IconProps) => <Base {...p}><path d="M3 10.5 12 3l9 7.5" /><path d="M5 9.5V21h14V9.5" /></Base>,
  Pos: (p: IconProps) => <Base {...p}><rect x="3" y="4" width="18" height="12" rx="2" /><path d="M7 20h10M12 16v4" /></Base>,
  Box: (p: IconProps) => <Base {...p}><path d="m12 3 8 4.5v9L12 21l-8-4.5v-9L12 3Z" /><path d="M4 7.5 12 12l8-4.5M12 12v9" /></Base>,
  Layers: (p: IconProps) => <Base {...p}><path d="m12 3 9 5-9 5-9-5 9-5Z" /><path d="m3 13 9 5 9-5M3 18l9 5 9-5" /></Base>,
  Cart: (p: IconProps) => <Base {...p}><circle cx="9" cy="20" r="1.4" /><circle cx="18" cy="20" r="1.4" /><path d="M2 3h3l2.4 12.4a2 2 0 0 0 2 1.6h8.2a2 2 0 0 0 2-1.6L23 7H5.5" /></Base>,
  Users: (p: IconProps) => <Base {...p}><circle cx="9" cy="8" r="3.2" /><path d="M2.5 20a6.5 6.5 0 0 1 13 0" /><path d="M17 5.2a3.2 3.2 0 0 1 0 6M21.5 20a6.2 6.2 0 0 0-4-5.8" /></Base>,
  Truck: (p: IconProps) => <Base {...p}><path d="M3 6h11v9H3zM14 9h4l3 3v3h-7" /><circle cx="7" cy="18" r="1.6" /><circle cx="17" cy="18" r="1.6" /></Base>,
  Wallet: (p: IconProps) => <Base {...p}><path d="M3 7a2 2 0 0 1 2-2h13v4" /><path d="M3 7v10a2 2 0 0 0 2 2h15V7H5" /><circle cx="16.5" cy="12.5" r="1.3" /></Base>,
  Ledger: (p: IconProps) => <Base {...p}><path d="M5 3h11l4 4v14H5z" /><path d="M9 8h6M9 12h6M9 16h4" /></Base>,
  Chart: (p: IconProps) => <Base {...p}><path d="M4 20V4M20 20H4" /><path d="M8 16v-4M12 16V8M16 16v-6" /></Base>,
  Group: (p: IconProps) => <Base {...p}><rect x="3" y="4" width="7" height="7" rx="1.5" /><rect x="14" y="4" width="7" height="7" rx="1.5" /><rect x="8.5" y="14" width="7" height="7" rx="1.5" /></Base>,
  Plug: (p: IconProps) => <Base {...p}><path d="M9 3v6M15 3v6" /><path d="M6 9h12v3a6 6 0 0 1-12 0z" /><path d="M12 18v3" /></Base>,
  Shield: (p: IconProps) => <Base {...p}><path d="M12 3 5 6v6c0 4.5 3 7.5 7 9 4-1.5 7-4.5 7-9V6l-7-3Z" /><path d="m9 12 2 2 4-4" /></Base>,
  Sync: (p: IconProps) => <Base {...p}><path d="M4 10a8 8 0 0 1 13.7-4.6L20 8" /><path d="M20 4v4h-4M20 14a8 8 0 0 1-13.7 4.6L4 16" /><path d="M4 20v-4h4" /></Base>,
  Sliders: (p: IconProps) => <Base {...p}><path d="M4 7h11M19 7h1M4 17h4M12 17h8" /><circle cx="17" cy="7" r="2" /><circle cx="10" cy="17" r="2" /></Base>,
  Cog: (p: IconProps) => <Base {...p}><circle cx="12" cy="12" r="3.2" /><path d="M12 2v3M12 19v3M4.2 4.2l2.1 2.1M17.7 17.7l2.1 2.1M2 12h3M19 12h3M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1" /></Base>,
  Search: (p: IconProps) => <Base {...p}><circle cx="11" cy="11" r="6.5" /><path d="m20 20-4.3-4.3" /></Base>,
  Bell: (p: IconProps) => <Base {...p}><path d="M6 10a6 6 0 0 1 12 0c0 5 2 6 2 6H4s2-1 2-6Z" /><path d="M10 20a2 2 0 0 0 4 0" /></Base>,
  Sun: (p: IconProps) => <Base {...p}><circle cx="12" cy="12" r="4" /><path d="M12 2v2M12 20v2M4 12H2M22 12h-2M5 5l1.5 1.5M17.5 17.5 19 19M19 5l-1.5 1.5M6.5 17.5 5 19" /></Base>,
  Moon: (p: IconProps) => <Base {...p}><path d="M20 13.5A8 8 0 0 1 10.5 4 8 8 0 1 0 20 13.5Z" /></Base>,
  Check: (p: IconProps) => <Base {...p}><path d="m5 13 4 4L19 7" /></Base>,
  Alert: (p: IconProps) => <Base {...p}><path d="M12 3 2 20h20L12 3Z" /><path d="M12 10v4M12 17h.01" /></Base>,
  Info: (p: IconProps) => <Base {...p}><circle cx="12" cy="12" r="9" /><path d="M12 11v5M12 8h.01" /></Base>,
  X: (p: IconProps) => <Base {...p}><path d="M6 6 18 18M18 6 6 18" /></Base>,
  ChevronDown: (p: IconProps) => <Base {...p}><path d="m6 9 6 6 6-6" /></Base>,
  Logout: (p: IconProps) => <Base {...p}><path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4" /><path d="M16 17l5-5-5-5M21 12H9" /></Base>,
  Star: (p: IconProps) => <Base {...p}><path d="m12 3 2.6 5.6 6 .8-4.4 4.2 1.1 6L12 17l-5.3 2.6 1.1-6L3.4 9.4l6-.8L12 3Z" /></Base>,
  Offline: (p: IconProps) => <Base {...p}><path d="M2 2 22 22M8.5 8.5A6 6 0 0 0 4 10M12 4a8 8 0 0 1 8 6M6.3 12.3A4 4 0 0 0 5 14M15 12a4 4 0 0 1 1.7 1.4M9 16a3 3 0 0 1 5 0" /><path d="M12 20h.01" /></Base>,
  Activity: (p: IconProps) => <Base {...p}><path d="M3 12h4l3 8 4-16 3 8h4" /></Base>,
  Bulb: (p: IconProps) => <Base {...p}><path d="M9 18h6M10 21h4" /><path d="M12 3a6 6 0 0 0-3.5 10.9c.6.5 1 1.2 1 2.1h5c0-.9.4-1.6 1-2.1A6 6 0 0 0 12 3Z" /></Base>,
  MapPin: (p: IconProps) => <Base {...p}><path d="M12 21s7-6.5 7-11.5A7 7 0 0 0 5 9.5C5 14.5 12 21 12 21Z" /><circle cx="12" cy="9.5" r="2.3" /></Base>,
  Download: (p: IconProps) => <Base {...p}><path d="M12 3v12M7 10l5 5 5-5" /><path d="M4 17v3h16v-3" /></Base>,
  Printer: (p: IconProps) => <Base {...p}><path d="M7 8V3h10v5" /><rect x="3" y="8" width="18" height="9" rx="2" /><path d="M7 14h10v7H7z" /></Base>,
  Briefcase: (p: IconProps) => <Base {...p}><rect x="3" y="7" width="18" height="13" rx="2" /><path d="M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2M3 12h18" /></Base>,
  IdCard: (p: IconProps) => <Base {...p}><rect x="3" y="5" width="18" height="14" rx="2" /><circle cx="9" cy="11" r="2" /><path d="M6 16a3 3 0 0 1 6 0M14 10h4M14 13h3" /></Base>,
  Folder: (p: IconProps) => <Base {...p}><path d="M3 7a1 1 0 0 1 1-1h4l2 2h10a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V7Z" /></Base>,
  // NEXORA ONE — intelligence & NEXORA AI
  Sparkles: (p: IconProps) => <Base {...p}><path d="M12 3.5 13.8 9 19.5 11l-5.7 2L12 18.5 10.2 13 4.5 11l5.7-2L12 3.5Z" /><path d="M19 3v3M17.5 4.5h3M5 17v3M3.5 18.5h3" /></Base>,
  Send: (p: IconProps) => <Base {...p}><path d="M4 12 20 4l-6 16-3-7-7-1Z" /><path d="m11 13 9-9" /></Base>,
  ArrowRight: (p: IconProps) => <Base {...p}><path d="M5 12h14M13 6l6 6-6 6" /></Base>,
  Clock: (p: IconProps) => <Base {...p}><circle cx="12" cy="12" r="8.5" /><path d="M12 7.5V12l3 2" /></Base>,
  Trend: (p: IconProps) => <Base {...p}><path d="m3 17 6-6 4 4 8-8" /><path d="M15 7h6v6" /></Base>,
  Automation: (p: IconProps) => <Base {...p}><path d="M13 3 5 14h6l-1 7 8-11h-6l1-7Z" /></Base>,
  Monogram: (p: IconProps) => (
    <Base {...p} strokeWidth={2}>
      <path d="M5 19V5l14 14V5" />
    </Base>
  ),
};

export type IconName = keyof typeof Icon;
