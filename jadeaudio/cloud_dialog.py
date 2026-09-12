"""The "Online presets" window: browse FiiO's library, and sign in to reach
the presets saved on your own account.

Browsing and searching need no account. Signing in is entirely the user's
doing: they type their own username, password and CAPTCHA here. The password
goes straight to FiiO's token endpoint and is never stored, and the CAPTCHA is
shown as an image for them to read.
"""

from __future__ import annotations

import io
import tkinter as tk
import webbrowser
from tkinter import messagebox, ttk

from . import cloud as cloud_mod
from . import protocol as p

try:
    from PIL import Image, ImageTk
except ImportError:  # pragma: no cover - Pillow ships with the executable
    Image = ImageTk = None


class LoginDialog(tk.Toplevel):
    """Collects the user's own FiiO credentials and hands them to the server."""

    def __init__(self, parent: "CloudDialog", on_success):
        super().__init__(parent)
        self.parent = parent
        self.on_success = on_success
        self.cloud = parent.cloud
        self.captcha_token = ""
        self._captcha_image = None

        self.title("Sign in to FiiO")
        self.configure(bg=parent.theme["BG"])
        self.transient(parent)
        self.resizable(False, False)
        self.grab_set()

        body = ttk.Frame(self, padding=16)
        body.pack(fill="both", expand=True)

        ttk.Label(body, text="Sign in", style="Title.TLabel").grid(
            row=0, column=0, columnspan=2, sticky="w"
        )
        ttk.Label(
            body,
            text="Your FiiO account, as used on fiiocontrol.fiio.com.",
            style="Muted.TLabel",
        ).grid(row=1, column=0, columnspan=2, sticky="w", pady=(0, 12))

        ttk.Label(body, text="Username").grid(row=2, column=0, sticky="w", pady=(0, 2))
        self.username = tk.StringVar()
        user_entry = ttk.Entry(body, textvariable=self.username, width=32)
        user_entry.grid(row=3, column=0, columnspan=2, sticky="ew")

        ttk.Label(body, text="Password").grid(row=4, column=0, sticky="w", pady=(10, 2))
        self.password = tk.StringVar()
        self.password_entry = ttk.Entry(body, textvariable=self.password, show="•", width=32)
        self.password_entry.grid(row=5, column=0, columnspan=2, sticky="ew")
        self.reveal = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            body, text="Show password", variable=self.reveal, command=self._toggle_reveal
        ).grid(row=6, column=0, sticky="w", pady=(4, 0))

        ttk.Label(body, text="Verification code").grid(row=7, column=0, sticky="w", pady=(10, 2))
        code_row = ttk.Frame(body)
        code_row.grid(row=8, column=0, columnspan=2, sticky="ew")
        self.captcha = tk.StringVar()
        code_entry = ttk.Entry(code_row, textvariable=self.captcha, width=12)
        code_entry.pack(side="left")
        self.captcha_label = tk.Label(
            code_row,
            text="loading...",
            bg=parent.theme["PANEL"],
            fg=parent.theme["FG"],
            width=120,
            height=44,
            cursor="hand2",
        )
        self.captcha_label.pack(side="left", padx=(8, 0))
        self.captcha_label.bind("<Button-1>", lambda _e: self.refresh_captcha())
        ttk.Button(code_row, text="Refresh", command=self.refresh_captcha).pack(
            side="left", padx=(8, 0)
        )
        ttk.Label(body, text="Click the image for a different one.", style="Muted.TLabel").grid(
            row=9, column=0, columnspan=2, sticky="w", pady=(4, 0)
        )

        self.agree = tk.BooleanVar(value=False)
        agree_row = ttk.Frame(body)
        agree_row.grid(row=10, column=0, columnspan=2, sticky="w", pady=(12, 0))
        ttk.Checkbutton(
            agree_row, text="I agree to FiiO's", variable=self.agree
        ).pack(side="left")
        for label, url in (
            ("Privacy Policy", "https://www.fiio.com/privacypolicy"),
            ("User Agreement", "https://www.fiio.com/yhxy"),
        ):
            link = tk.Label(
                agree_row,
                text=label,
                bg=parent.theme["BG"],
                fg=parent.theme["ACCENT"],
                cursor="hand2",
                font=("Segoe UI", 9, "underline"),
            )
            link.pack(side="left", padx=(6, 0))
            link.bind("<Button-1>", lambda _e, u=url: webbrowser.open(u))

        self.status = tk.StringVar(value="")
        ttk.Label(body, textvariable=self.status, style="Muted.TLabel").grid(
            row=11, column=0, columnspan=2, sticky="w", pady=(10, 0)
        )

        actions = ttk.Frame(body)
        actions.grid(row=12, column=0, columnspan=2, sticky="ew", pady=(10, 0))
        self.sign_in_btn = ttk.Button(actions, text="Sign in", command=self.submit)
        self.sign_in_btn.pack(side="left")
        ttk.Button(
            actions, text="Create account", command=lambda: webbrowser.open(cloud_mod.REGISTER_URL)
        ).pack(side="left", padx=(8, 0))
        ttk.Button(actions, text="Cancel", command=self.destroy).pack(side="right")

        ttk.Label(
            body,
            text="Your password is sent to FiiO and kept nowhere else - not on disk, not in a log.",
            style="Muted.TLabel",
            wraplength=340,
        ).grid(row=13, column=0, columnspan=2, sticky="w", pady=(12, 0))

        body.columnconfigure(0, weight=1)
        code_entry.bind("<Return>", lambda _e: self.submit())
        user_entry.focus_set()
        self.after(80, self.refresh_captcha)

    def _toggle_reveal(self) -> None:
        self.password_entry.configure(show="" if self.reveal.get() else "•")

    def refresh_captcha(self) -> None:
        self.captcha_label.configure(text="loading...", image="", width=120, height=44)
        self.captcha.set("")

        def work():
            return self.cloud.auth.captcha()

        def done(result):
            image_bytes, token = result
            self.captcha_token = token
            if ImageTk is None:
                self.captcha_label.configure(text="(no image support)")
                return
            image = Image.open(io.BytesIO(image_bytes))
            scale = max(1, round(44 / max(image.height, 1)))
            image = image.resize((image.width * scale, image.height * scale), Image.LANCZOS)
            self._captcha_image = ImageTk.PhotoImage(image)
            # width/height count characters until an image is attached, so set
            # them in pixels here or the picture gets clipped.
            self.captcha_label.configure(
                image=self._captcha_image, text="", width=image.width, height=image.height
            )

        def failed(exc):
            self.captcha_label.configure(text="failed")
            self.status.set(str(exc))

        self.parent.app.worker.submit(work, done, failed)

    def submit(self) -> None:
        if not self.agree.get():
            self.status.set("Tick the agreement box first.")
            return
        username = self.username.get().strip()
        password = self.password.get()
        code = self.captcha.get().strip()
        if not username or not password or not code:
            self.status.set("Username, password and verification code are all needed.")
            return

        self.status.set("Signing in...")
        self.sign_in_btn.configure(state="disabled")
        token = self.captcha_token

        def work():
            self.cloud.auth.login(username, password, code, token)
            return self.cloud.auth.user_name or username

        def done(name):
            self.destroy()
            self.on_success(name)

        def failed(exc):
            self.status.set(str(exc))
            self.sign_in_btn.configure(state="normal")
            self.password.set("")
            self.refresh_captcha()

        self.parent.app.worker.submit(work, done, failed)


class PresetList(ttk.Frame):
    """A table of presets plus the detail pane underneath it."""

    def __init__(self, master, dialog: "CloudDialog", loader, searchable: bool = False):
        super().__init__(master, padding=10)
        self.dialog = dialog
        self.loader = loader
        self.presets: list = []
        self.page = 1
        self.page_size = 20
        self.total = 0

        top = ttk.Frame(self)
        top.pack(fill="x")
        self.search_var = tk.StringVar()
        if searchable:
            entry = ttk.Entry(top, textvariable=self.search_var)
            entry.pack(side="left", fill="x", expand=True)
            entry.bind("<Return>", lambda _e: self.reload(search=True))
            ttk.Button(top, text="Search", command=lambda: self.reload(search=True)).pack(
                side="left", padx=(6, 0)
            )
            ttk.Button(top, text="Clear", command=self.clear_search).pack(side="left", padx=(6, 0))
        ttk.Button(top, text="Refresh", command=self.reload).pack(side="right")

        table = ttk.Frame(self)
        table.pack(fill="both", expand=True, pady=(8, 0))
        columns = ("name", "author", "downloads", "bands")
        self.tree = ttk.Treeview(table, columns=columns, show="headings", height=12)
        scroll = ttk.Scrollbar(table, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=scroll.set)
        for key, text, width in (
            ("name", "Preset", 260),
            ("author", "By", 130),
            ("downloads", "Downloads", 80),
            ("bands", "Bands", 55),
        ):
            self.tree.heading(key, text=text)
            self.tree.column(key, width=width, anchor="w" if key in ("name", "author") else "center")
        self.tree.pack(side="left", fill="both", expand=True)
        scroll.pack(side="right", fill="y")
        self.tree.bind("<<TreeviewSelect>>", self._on_select)
        self.tree.bind("<Double-1>", lambda _e: self.apply())

        nav = ttk.Frame(self)
        nav.pack(fill="x", pady=(6, 0))
        self.prev_btn = ttk.Button(nav, text="< Prev", command=lambda: self.step(-1))
        self.prev_btn.pack(side="left")
        self.page_label = ttk.Label(nav, text="", style="Muted.TLabel")
        self.page_label.pack(side="left", padx=8)
        self.next_btn = ttk.Button(nav, text="Next >", command=lambda: self.step(1))
        self.next_btn.pack(side="left")
        self.apply_btn = ttk.Button(nav, text="Apply to device", command=self.apply, state="disabled")
        self.apply_btn.pack(side="right")

        self.detail = tk.Text(
            self,
            height=7,
            bg=dialog.theme["PANEL"],
            fg=dialog.theme["FG"],
            bd=0,
            padx=10,
            pady=8,
            wrap="word",
            font=("Consolas", 9),
        )
        self.detail.pack(fill="x", pady=(8, 0))
        self.detail.configure(state="disabled")

        self.status = tk.StringVar(value="")
        ttk.Label(self, textvariable=self.status, style="Muted.TLabel").pack(anchor="w", pady=(6, 0))

    # -- data ----------------------------------------------------------------

    def clear_search(self) -> None:
        self.search_var.set("")
        self.page = 1
        self.reload()

    def step(self, delta: int) -> None:
        self.page = max(1, self.page + delta)
        self.reload()

    def reload(self, search: bool = False) -> None:
        if search:
            self.page = 1
        keyword = self.search_var.get().strip()
        self.status.set("Loading...")
        self.apply_btn.configure(state="disabled")
        page, size = self.page, self.page_size

        def work():
            return self.loader(page, size, keyword)

        def done(result):
            self.presets, self.total = result
            self.tree.delete(*self.tree.get_children())
            for i, item in enumerate(self.presets):
                self.tree.insert(
                    "",
                    "end",
                    iid=str(i),
                    values=(item.name, item.author, item.downloads, len(item.bands)),
                )
            pages = max(1, -(-self.total // size)) if self.total else 1
            self.page_label.configure(text=f"page {self.page} of {pages}   ({self.total} presets)")
            self.prev_btn.configure(state="normal" if self.page > 1 else "disabled")
            self.next_btn.configure(state="normal" if self.page < pages else "disabled")
            self.status.set("" if self.presets else "Nothing here.")
            self._show("")

        def failed(exc):
            self.tree.delete(*self.tree.get_children())
            self.status.set(str(exc))
            self._show(str(exc))

        self.dialog.app.worker.submit(work, done, failed)

    # -- detail --------------------------------------------------------------

    def _show(self, text: str) -> None:
        self.detail.configure(state="normal")
        self.detail.delete("1.0", "end")
        self.detail.insert("1.0", text)
        self.detail.configure(state="disabled")

    def selected(self):
        rows = self.tree.selection()
        if not rows:
            return None
        index = int(rows[0])
        return self.presets[index] if index < len(self.presets) else None

    def _on_select(self, _event=None) -> None:
        preset = self.selected()
        if preset is None:
            return
        self.apply_btn.configure(state="normal")
        wanted = self.dialog.device_type
        warning = ""
        if preset.device_type != wanted:
            warning = (
                f"\n\nMade for device type {preset.device_type}, not your "
                f"{self.dialog.device_name} ({wanted})."
            )
        bands = "\n".join(
            f"   {i + 1}. {b['frequency']:>6} Hz  {b['gain']:+6.1f} dB  Q {b['q']:.2f}  "
            f"{p.FilterType(b['filter_type']).name.replace('_', ' ').lower()}"
            for i, b in enumerate(preset.bands)
        )
        tags = f"   [{', '.join(preset.tags)}]" if preset.tags else ""
        self._show(
            f"{preset.name}{tags}\n"
            f"by {preset.author}   -   {preset.downloads} downloads\n"
            f"{preset.share_code}\n\n"
            f"{preset.description}\n\n"
            f"Global gain {preset.global_gain:+.1f} dB, {len(preset.bands)} bands:\n"
            f"{bands}{warning}"
        )

    def apply(self) -> None:
        preset = self.selected()
        if preset is not None:
            self.dialog.app.apply_cloud_preset(preset)


class CloudDialog(tk.Toplevel):
    def __init__(self, app, theme: dict):
        super().__init__(app.root)
        self.app = app
        self.theme = theme
        self.cloud = cloud_mod.Cloud()

        self.device_name = app.dev.product_name if app.dev else ""
        self.device_type = cloud_mod.device_type_for(self.device_name)

        self.title("Online presets")
        self.configure(bg=theme["BG"])
        self.transient(app.root)
        self.geometry("760x620")
        self.minsize(680, 540)

        header = ttk.Frame(self, padding=(12, 10, 12, 0))
        header.pack(fill="x")
        ttk.Label(header, text="Online presets", style="Title.TLabel").pack(side="left")
        self.account_var = tk.StringVar(value="Not signed in")
        ttk.Label(header, textvariable=self.account_var, style="Muted.TLabel").pack(
            side="right", padx=(0, 8)
        )
        self.account_btn = ttk.Button(header, text="Sign in", command=self.toggle_account)
        self.account_btn.pack(side="right")

        ttk.Label(
            self,
            text=f"Showing presets for {self.device_name} (device type {self.device_type}).",
            style="Muted.TLabel",
        ).pack(anchor="w", padx=12, pady=(2, 8))

        self.tabs = ttk.Notebook(self)
        self.tabs.pack(fill="both", expand=True, padx=12, pady=(0, 6))

        self.community = PresetList(self.tabs, self, self._load_community, searchable=True)
        self.official = PresetList(self.tabs, self, self._load_official)
        self.personal = PresetList(self.tabs, self, self._load_personal)
        self.tabs.add(self.community, text="Handpick")
        self.tabs.add(self.official, text="Official")
        self.tabs.add(self.personal, text="My presets")
        self.tabs.add(self._build_share_tab(), text="Share code")

        ttk.Button(self, text="Close", command=self.destroy).pack(anchor="e", padx=12, pady=(0, 10))

        self.after(120, self.community.reload)

    # -- loaders -------------------------------------------------------------

    def _load_community(self, page: int, size: int, keyword: str):
        if keyword:
            hits = self.cloud.search(keyword, self.device_type)
            return hits, len(hits)
        return self.cloud.community_presets(self.device_type, page, size)

    def _load_official(self, page: int, size: int, _keyword: str):
        return self.cloud.official_presets(self.device_type, page, size)

    def _load_personal(self, _page: int, _size: int, _keyword: str):
        items = self.cloud.personal_presets(self.device_type)
        return items, len(items)

    # -- share code tab ------------------------------------------------------

    def _build_share_tab(self) -> ttk.Frame:
        frame = ttk.Frame(self.tabs, padding=10)
        ttk.Label(frame, text="Paste a share code", style="Title.TLabel").pack(anchor="w")
        ttk.Label(
            frame,
            text="Codes look like FiiOSC- followed by 32 hex characters.",
            style="Muted.TLabel",
        ).pack(anchor="w", pady=(0, 8))

        row = ttk.Frame(frame)
        row.pack(fill="x")
        self.code_var = tk.StringVar()
        entry = ttk.Entry(row, textvariable=self.code_var)
        entry.pack(side="left", fill="x", expand=True)
        entry.bind("<Return>", lambda _e: self._fetch_code())
        ttk.Button(row, text="Fetch", command=self._fetch_code).pack(side="left", padx=(8, 0))

        self.code_detail = tk.Text(
            frame,
            height=14,
            bg=self.theme["PANEL"],
            fg=self.theme["FG"],
            bd=0,
            padx=10,
            pady=8,
            wrap="word",
            font=("Consolas", 9),
        )
        self.code_detail.pack(fill="both", expand=True, pady=(10, 0))
        self.code_detail.configure(state="disabled")

        self.code_apply = ttk.Button(
            frame, text="Apply to device", command=self._apply_code, state="disabled"
        )
        self.code_apply.pack(anchor="w", pady=(8, 0))
        self._code_preset = None
        return frame

    def _fetch_code(self) -> None:
        code = self.code_var.get().strip()
        if not code:
            return
        self.code_apply.configure(state="disabled")

        def work():
            return self.cloud.by_share_code(code)

        def done(preset):
            self._code_preset = preset
            bands = "\n".join(
                f"   {i + 1}. {b['frequency']:>6} Hz  {b['gain']:+6.1f} dB  Q {b['q']:.2f}  "
                f"{p.FilterType(b['filter_type']).name.replace('_', ' ').lower()}"
                for i, b in enumerate(preset.bands)
            )
            self._set_code_detail(
                f"{preset.name}\nby {preset.author}   -   {preset.downloads} downloads\n\n"
                f"{preset.description}\n\n"
                f"Global gain {preset.global_gain:+.1f} dB, {len(preset.bands)} bands:\n{bands}"
            )
            self.code_apply.configure(state="normal")

        def failed(exc):
            self._set_code_detail(str(exc))

        self.app.worker.submit(work, done, failed)

    def _set_code_detail(self, text: str) -> None:
        self.code_detail.configure(state="normal")
        self.code_detail.delete("1.0", "end")
        self.code_detail.insert("1.0", text)
        self.code_detail.configure(state="disabled")

    def _apply_code(self) -> None:
        if self._code_preset is not None:
            self.app.apply_cloud_preset(self._code_preset)

    # -- account -------------------------------------------------------------

    def toggle_account(self) -> None:
        if self.cloud.auth.logged_in:
            self.cloud.auth.logout()
            self.account_var.set("Not signed in")
            self.account_btn.configure(text="Sign in")
            self.personal.tree.delete(*self.personal.tree.get_children())
            self.personal.status.set("Signed out.")
            return
        LoginDialog(self, self._signed_in)

    def _signed_in(self, name: str) -> None:
        self.account_var.set(f"Signed in as {name}")
        self.account_btn.configure(text="Sign out")
        self.tabs.select(self.personal)
        self.personal.reload()
