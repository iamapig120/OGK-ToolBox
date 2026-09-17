import { useEffect, useState } from "react";

export type LayoutMode = "auto" | "standard" | "portrait";
const storageKey = "ogk-toolbox.layout.v1";

export function useLayout() {
  const [layout, setLayoutState] = useState<LayoutMode>(() => {
    const saved = localStorage.getItem(storageKey);
    return saved === "standard" || saved === "portrait" ? saved : "auto";
  });
  const [portraitWindow, setPortraitWindow] = useState(() => matchMedia("(orientation: portrait)").matches);
  useEffect(() => {
    const query = matchMedia("(orientation: portrait)");
    const update = () => setPortraitWindow(query.matches);
    query.addEventListener("change", update);
    return () => query.removeEventListener("change", update);
  }, []);
  const setLayout = (value: LayoutMode) => {
    setLayoutState(value);
    localStorage.setItem(storageKey, value);
  };
  return { layout, setLayout, portrait: layout === "portrait" || (layout === "auto" && portraitWindow) };
}
