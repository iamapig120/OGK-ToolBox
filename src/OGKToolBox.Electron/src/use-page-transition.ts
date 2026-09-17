import { useEffect, useRef, useState } from "react";
import { flushSync } from "react-dom";

/** A delayed snapshot must never restore a superseded route. */
export function usePageTransition<T extends string>(initialPage: T) {
  const [page, setPage] = useState(initialPage);
  const requestedPage = useRef(initialPage);
  const sequence = useRef(0);
  const active = useRef<ViewTransition | null>(null);

  useEffect(() => {
    const preference = window.matchMedia("(prefers-reduced-motion: reduce)");
    const stop = () => active.current?.skipTransition();
    const onPreferenceChange = () => { if (preference.matches) stop(); };
    const onVisibilityChange = () => { if (document.hidden) stop(); };
    preference.addEventListener("change", onPreferenceChange);
    document.addEventListener("visibilitychange", onVisibilityChange);
    return () => {
      sequence.current++;
      stop();
      delete document.documentElement.dataset.pageTransition;
      preference.removeEventListener("change", onPreferenceChange);
      document.removeEventListener("visibilitychange", onVisibilityChange);
    };
  }, []);

  const navigate = (next: T) => {
    if (next === requestedPage.current) return;
    requestedPage.current = next;
    const request = ++sequence.current;
    active.current?.skipTransition();
    active.current = null;
    const commit = () => {
      if (sequence.current !== request) return;
      flushSync(() => setPage(next));
      document.querySelector(".workspace")?.scrollTo({ top: 0, left: 0, behavior: "instant" });
    };
    if (!document.startViewTransition || document.hidden ||
        window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
      delete document.documentElement.dataset.pageTransition;
      commit();
      return;
    }
    document.documentElement.dataset.pageTransition = "native";
    try {
      const transition = document.startViewTransition(commit);
      active.current = transition;
      // Skipping rejects ready, but the update callback still runs.
      void transition.ready.catch(() => {});
      const cleanUp = () => {
        if (sequence.current !== request) return;
        active.current = null;
        // Keep native mode after completion so the fallback does not replay.
      };
      void transition.finished.then(cleanUp, cleanUp);
    } catch {
      delete document.documentElement.dataset.pageTransition;
      commit();
    }
  };

  return [page, navigate] as const;
}
