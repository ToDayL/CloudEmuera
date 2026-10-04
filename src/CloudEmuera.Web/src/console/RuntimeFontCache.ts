const DATABASE = "cloudemuera-runtime-fonts";
const STORE = "faces";
const MAX_FACES = 6;
const STORAGE_TIMEOUT_MS = 1_500;

interface CachedFont { digest: string; bytes: ArrayBuffer; lastUsed: number }

/** Best-effort storage: private browsing, quota and blocked upgrades must not
 * become another prerequisite for downloading or displaying a font. */
function access<T>(operation: (store: IDBObjectStore, result: (value: T) => void) => void): Promise<T | null> {
  return new Promise(resolve => {
    let database: IDBDatabase | undefined;
    let transaction: IDBTransaction | undefined;
    let finished = false;
    const finish = (value: T | null) => {
      if (finished) return;
      finished = true;
      clearTimeout(timeout);
      database?.close();
      resolve(value);
    };
    const timeout = setTimeout(() => {
      try { transaction?.abort(); } catch { /* Transaction may have just completed. */ }
      finish(null);
    }, STORAGE_TIMEOUT_MS);
    try {
      const request = indexedDB.open(DATABASE, 1);
      request.onupgradeneeded = () => {
        const store = request.result.createObjectStore(STORE, { keyPath: "digest" });
        store.createIndex("lastUsed", "lastUsed");
      };
      request.onerror = () => finish(null);
      request.onblocked = () => finish(null);
      request.onsuccess = () => {
        database = request.result;
        if (finished) { database.close(); return; }
        try {
          transaction = database.transaction(STORE, "readwrite");
          let value: T | null = null;
          transaction.oncomplete = () => finish(value);
          transaction.onerror = transaction.onabort = () => finish(null);
          operation(transaction.objectStore(STORE), result => { value = result; });
        } catch { finish(null); }
      };
    } catch { finish(null); }
  });
}

export function readRuntimeFontBytes(digest: string): Promise<ArrayBuffer | null> {
  return access((store, result) => {
    const request = store.get(digest);
    request.onsuccess = () => {
      const cached = request.result as CachedFont | undefined;
      if (!(cached?.bytes instanceof ArrayBuffer)) return;
      store.put({ ...cached, lastUsed: Date.now() });
      result(cached.bytes);
    };
  });
}

export function writeRuntimeFontBytes(digest: string, bytes: ArrayBuffer): Promise<boolean | null> {
  return access((store, result) => {
    store.put({ digest, bytes, lastUsed: Date.now() } satisfies CachedFont);
    const count = store.count();
    count.onsuccess = () => {
      let excess = count.result - MAX_FACES;
      if (excess <= 0) return;
      const cursor = store.index("lastUsed").openKeyCursor();
      cursor.onsuccess = () => {
        if (!cursor.result || excess <= 0) return;
        // Equal timestamps must never evict the face just written.
        if (cursor.result.primaryKey !== digest) { store.delete(cursor.result.primaryKey); excess--; }
        cursor.result.continue();
      };
    };
    result(true);
  });
}

export function deleteRuntimeFontBytes(digest: string): Promise<boolean | null> {
  return access((store, result) => { store.delete(digest); result(true); });
}
