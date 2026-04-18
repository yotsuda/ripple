;;;; ripple Steel Bank Common Lisp REPL integration — installs OSC 633
;;;; emission into sb-int:*repl-prompt-fun* so ripple can track command
;;;; boundaries the same way it does for CCL and ABCL.
;;;;
;;;; Event sequence per command (after the first prompt):
;;;;   ? user types an expression → Enter →
;;;;   SBCL reads + evals + prints →
;;;;   the REPL loop calls (funcall sb-int:*repl-prompt-fun* stream) →
;;;;   our wrapper prints "OSC D;0  OSC P;Cwd=...  OSC A  ? " →
;;;;   ripple's OscParser sees the markers and resolves the command.
;;;;
;;;; SBCL's default prompt is "* " (star + space). We override to the
;;;; literal "? " so all three Common Lisp adapters (CCL, ABCL, SBCL)
;;;; share the same main-mode regex ^\? $ and the worker's ModeDetector
;;;; stays impl-agnostic.
;;;;
;;;; No OSC B / OSC C — SBCL has no stdlib pre-input hook. Input echo
;;;; is stripped deterministically the same way CCL / ABCL do.

(in-package :cl-user)

(defparameter *ripple-first-prompt* t)
(defparameter *ripple-last-cwd* nil)

(defun ripple-cwd ()
  (handler-case (namestring *default-pathname-defaults*)
    (error () nil)))

(defun ripple-sbcl-prompt (stream)
  (let ((esc (code-char #x1B))
        (bel (code-char #x07)))
    ;; D;0 between commands. SBCL has no per-expression exit code;
    ;; same convention as CCL / Python / Racket.
    (unless *ripple-first-prompt*
      (format stream "~C]633;D;0~C" esc bel))
    (setf *ripple-first-prompt* nil)
    ;; P;Cwd when the working directory changed since the last prompt.
    (let ((cwd (ripple-cwd)))
      (when (and cwd (not (equal cwd *ripple-last-cwd*)))
        (format stream "~C]633;P;Cwd=~A~C" esc cwd bel)
        (setf *ripple-last-cwd* cwd)))
    ;; A;prompt-start then the literal "? " both CCL and ABCL use.
    (format stream "~C]633;A~C? " esc bel)
    (force-output stream)))

;; Unlike CCL (which hard-locks redefinition of kernel functions), SBCL
;; exposes the REPL prompt as a plain special variable — `setf` just
;; works. No warn-variable rebind needed.
(setf sb-int:*repl-prompt-fun* #'ripple-sbcl-prompt)

;;; Multi-line command delivery helper. ripple writes the command body
;;; to a .ripple-exec-*.lisp tempfile and sends a single-line
;;; (ripple-exec-file "...") call so the whole block is one OSC A-to-A
;;; boundary regardless of how many top-level forms it contains.
;;; Definitions leak into CL-USER because LOAD uses the calling
;;; package's namespace.
(defun ripple-exec-file (path)
  (unwind-protect
       (load path)
    (handler-case (delete-file path)
      (error () nil))))

;;; Self-delete the integration tempfile so a long-running ripple process
;;; doesn't leave stale files in TEMP. Gated on filename matching
;;; ".ripple-integration-" so accidentally running this file from a
;;; developer checkout (e.g. probing SBCL with the canonical source)
;;; does NOT wipe the source out from under the repository. SBCL has
;;; already read and loaded this file by the time this form runs, so
;;; deleting it mid-load is safe in the worker case.
(handler-case
    (let ((p *load-pathname*))
      (when (and p
                 (search ".ripple-integration-" (namestring p)))
        (delete-file p)))
  (error () nil))
