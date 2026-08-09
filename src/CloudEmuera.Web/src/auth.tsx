import { createContext, ReactNode, useContext, useEffect, useMemo, useState } from "react";
import { apiRequest, getCsrfToken } from "./api";

export type CurrentUser = { id: string; username: string; email: string; role: "PLAYER" | "ADMIN"; status: "ACTIVE" | "DISABLED"; mustChangePassword: boolean; stateVersion: number };
export type UserPage = { items: CurrentUser[] };
export type CreateUserInput = { username: string; email: string; temporaryPassword: string; role: CurrentUser["role"] };
export type UpdateUserInput = { username?: string; email?: string; role?: CurrentUser["role"]; status?: "ACTIVE" | "DISABLED" };
type AuthContextValue = {
  user: CurrentUser | null;
  loading: boolean;
  login: (email: string, password: string, rememberMe: boolean) => Promise<CurrentUser>;
  logout: () => Promise<void>;
  changePassword: (currentPassword: string, newPassword: string) => Promise<void>;
  listUsers: () => Promise<CurrentUser[]>;
  createUser: (input: CreateUserInput) => Promise<CurrentUser>;
  updateUser: (id: string, stateVersion: number, input: UpdateUserInput) => Promise<CurrentUser>;
  resetUserPassword: (id: string, stateVersion: number, temporaryPassword: string) => Promise<void>;
};
const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children, initialUser }: { children: ReactNode; initialUser?: CurrentUser | null }) {
  const [user, setUser] = useState<CurrentUser | null>(initialUser ?? null); const [loading, setLoading] = useState(initialUser === undefined);
  useEffect(() => { if (initialUser !== undefined) return; apiRequest<CurrentUser>("/auth/me").then(setUser).catch(() => setUser(null)).finally(() => setLoading(false)); }, [initialUser]);
  const value = useMemo<AuthContextValue>(() => ({ user, loading,
    login: async (email, password, rememberMe) => { const token = await getCsrfToken(); const current = await apiRequest<CurrentUser>("/auth/login", { method: "POST", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token }, body: JSON.stringify({ email, password, rememberMe }) }); setUser(current); return current; },
    logout: async () => { const token = await getCsrfToken(); await apiRequest<void>("/auth/logout", { method: "POST", headers: { "X-CSRF-TOKEN": token } }); setUser(null); },
    changePassword: async (currentPassword, newPassword) => { const token = await getCsrfToken(); await apiRequest<void>("/auth/change-password", { method: "POST", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token }, body: JSON.stringify({ currentPassword, newPassword }) }); const current = await apiRequest<CurrentUser>("/auth/me"); setUser(current); },
    listUsers: async () => (await apiRequest<UserPage>("/admin/users")).items,
    createUser: async (input) => { const token = await getCsrfToken(); return apiRequest<CurrentUser>("/admin/users", { method: "POST", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token }, body: JSON.stringify(input) }); },
    updateUser: async (id, stateVersion, input) => { const token = await getCsrfToken(); return apiRequest<CurrentUser>(`/admin/users/${encodeURIComponent(id)}`, { method: "PATCH", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token, "If-Match": `\"${stateVersion}\"` }, body: JSON.stringify(input) }); },
    resetUserPassword: async (id, stateVersion, temporaryPassword) => { const token = await getCsrfToken(); await apiRequest<void>(`/admin/users/${encodeURIComponent(id)}:reset-password`, { method: "POST", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token, "If-Match": `\"${stateVersion}\"` }, body: JSON.stringify({ temporaryPassword }) }); },
  }), [user, loading]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() { const value = useContext(AuthContext); if (!value) throw new Error("AuthProvider is required"); return value; }
