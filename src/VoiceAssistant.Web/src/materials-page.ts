import "./style.css";
import { BrowserAuth } from "./auth.js";
import { mountMaterialsPage } from "./materials-page-controller.js";

mountMaterialsPage(new BrowserAuth());
