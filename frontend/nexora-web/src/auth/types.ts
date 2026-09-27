export interface CompanyMembership {
  companyId: string;
  companyName: string;
  groupId: string | null;
  roles: string[];
}

export interface CurrentUser {
  id: string;
  email: string;
  fullName: string | null;
  mfaEnabled: boolean;
  companies: CompanyMembership[];
  permissions: string[];
}

export interface AuthTokenResponse {
  accessToken: string;
  tokenType: string;
  expiresInSeconds: number;
  user: CurrentUser;
  /** Deuxième facteur requis (Phase 33 §8) — `accessToken` est alors vide. */
  mfaRequired?: boolean;
  mfaToken?: string | null;
  mfaExpiresAt?: string | null;
  /** La politique impose la MFA mais elle n'est pas encore activée (§37). */
  mfaEnrollmentRequired?: boolean;
}
