"use strict";

const status = document.querySelector("[data-status]");
const completion = document.querySelector("[data-completion]");
if (status && completion) {
  const updateCompletion = () => {
    const complete = status.value === "2";
    completion.disabled = !complete;
    if (!complete) completion.value = "";
  };
  status.addEventListener("change", updateCompletion);
  updateCompletion();
}

for (const form of document.querySelectorAll("form[data-unsaved]")) {
  let dirty = false;
  form.addEventListener("input", () => { dirty = true; });
  form.addEventListener("change", () => { dirty = true; });
  form.addEventListener("submit", () => { dirty = false; });
  window.addEventListener("beforeunload", event => {
    if (!dirty) return;
    event.preventDefault();
    event.returnValue = "";
  });
}

document.querySelector("[data-print]")?.addEventListener("click", () => window.print());
document.querySelector("[data-copy]")?.addEventListener("click", async () => {
  const source = document.querySelector("#report-text");
  const notice = document.querySelector("[data-copy-status]");
  try {
    if (!navigator.clipboard) throw new Error("Clipboard is unavailable.");
    await navigator.clipboard.writeText(source.value);
    notice.textContent = "Copied. Paste into your email.";
  } catch {
    const fallback = document.querySelector("[data-copy-fallback]");
    fallback.open = true;
    source.focus();
    source.select();
    notice.textContent = "Automatic copying is unavailable. The report text is selected below; copy it manually.";
  }
});
