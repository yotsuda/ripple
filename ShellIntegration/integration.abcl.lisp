;;;; splash Armed Bear Common Lisp REPL integration — installs OSC
;;;; 633 emission into ABCL's top-level REPL prompt hook so splash
;;;; can track command boundaries the same way it does for pwsh /
;;;; bash / zsh / python / node / racket / ccl.
;;;;
;;;; Event sequence per command (after the first prompt):
;;;;   ? user types an expression → Enter →
;;;;   ABCL reads + evals + prints →
;;;;   the REPL loop calls (funcall top-level::*repl-prompt-fun* stream) →
;;;;   our wrapper prints "OSC D;0  OSC P;Cwd=...  OSC A  ? " →
;;;;   splash's OscParser sees the markers and resolves the command.
;;;;
;;;; ABCL's default prompt is printed by the function stored in
;;;; `top-level::*repl-prompt-fun*` and looks like `CL-USER(N): `
;;;; with a monotonic form counter. We override to a simple `? `
;;;; (matching CCL) so the adapter-level mode regex `^\? $` works
;;;; for both Common Lisp adapters without per-impl branching.
;;;;
;;;; No OSC B / OSC C — ABCL has no stdlib pre-input hook. Input echo
;;;; is stripped deterministically the same way Python / Node / CCL do.
;;;;
;;;; **Debugger mode is intentionally NOT covered here.** ABCL's
;;;; `system::debug-loop` uses a separate prompt mechanism from
;;;; `top-level::*repl-prompt-fun*` and the nesting-level capture
;;;; pattern CCL uses (`1 > `, `2 > `, ...) does not apply as-is.
;;;; The adapter ships with only the main mode declared; adding
;;;; debugger mode is future work once ABCL's debug-loop internals
;;;; are researched against a real break scenario.

(in-package :cl-user)

(defparameter *splash-first-prompt* t)
(defparameter *splash-last-cwd* nil)

(defun splash-cwd ()
  (handler-case (namestring *default-pathname-defaults*)
    (error () nil)))

(defun splash-print-prompt (stream)
  (let ((esc (code-char #x1B))
        (bel (code-char #x07)))
    ;; D;0 between commands. ABCL has no per-expression exit code,
    ;; same as Python / Racket / CCL.
    (unless *splash-first-prompt*
      (format stream "~C]633;D;0~C" esc bel))
    (setf *splash-first-prompt* nil)
    ;; P;Cwd when the working directory changed since the last prompt.
    (let ((cwd (splash-cwd)))
      (when (and cwd (not (equal cwd *splash-last-cwd*)))
        (format stream "~C]633;P;Cwd=~A~C" esc cwd bel)
        (setf *splash-last-cwd* cwd)))
    ;; A;prompt-start.
    (format stream "~C]633;A~C" esc bel)
    ;; The visible prompt. Matches the main mode regex `^\? $` in
    ;; abcl.yaml — same literal CCL uses so both CL adapters share
    ;; prompt shape without per-impl branching in the detector.
    (format stream "? ")
    (force-output stream)))

;; ABCL's top-level REPL reads from `*repl-prompt-fun*` on every
;; prompt call. Rebinding here installs our hook for every
;; subsequent prompt — no kernel-redefine warning gate like CCL
;; needed because ABCL treats this as an ordinary dynamic variable.
(setf top-level::*repl-prompt-fun* #'splash-print-prompt)

;;; Multi-line command delivery helper. splash writes the command body
;;; to a .splash-exec-*.lisp tempfile and sends a single-line
;;; (splash-exec-file "...") call so the whole block is one OSC A-to-A
;;; boundary regardless of how many top-level forms it contains.
;;; Definitions leak into CL-USER because LOAD uses the calling
;;; package's namespace.
(defun splash-exec-file (path)
  (unwind-protect
       (load path)
    (handler-case (delete-file path)
      (error () nil))))

;;; Self-delete the integration tempfile so a long-running splash
;;; process doesn't leave stale files in TEMP. Gated on filename
;;; matching ".splash-integration-" so accidentally running this file
;;; from a developer checkout (e.g. probing ABCL with the canonical
;;; source) does NOT wipe the source out from under the repository.
;;; ABCL has already read and loaded this file by the time this form
;;; runs, so deleting it mid-load is safe in the worker case.
(handler-case
    (let ((p *load-pathname*))
      (when (and p
                 (search ".splash-integration-" (namestring p)))
        (delete-file p)))
  (error () nil))
