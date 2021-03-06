import composition = require("durandal/composition");

/*
 * A KnockoutJS binding handler that transforms a div into the auto-complete items container for a text box.
 *
 * Usage: 
 *           <input id="myAutoComplete" type="text" data-bind="value: mySearchValue, valueUpdate: 'afterkeydown'" />
 *           <div style="display: none" data-bind="autoComplete: '#myAutoComplete', foreach: yourOwnResults">
 *               <div data-bind="text: name"></div>
 *           </div>
 *
 * In the above sample, yourOwnResults is an array that you are responsible for populating. And 'name' is the property on the items in that array.
 */
class autoCompleteBindingHandler {

    static install() {
        if (!ko.bindingHandlers["autoComplete"]) {
            ko.bindingHandlers["autoComplete"] = new autoCompleteBindingHandler();

            // This tells Durandal to fire this binding handler only after composition 
            // is complete and attached to the DOM.
            // See http://durandaljs.com/documentation/Interacting-with-the-DOM/
            composition.addBindingHandler("autoComplete");
        }
    }

    // Called by Knockout a single time when the binding handler is setup.
    init(element: HTMLElement, valueAccessor: () => string, allBindings: () => any, viewModel, bindingContext: any) {
        var inputId = valueAccessor();
        var input = $(inputId);
        if (input.length !== 1) {
            // Don't throw an error here, because we may cancel navigation, and Durandal may pull the element out.
            // Instead, we'll just issue a warning in the console and return.
            //throw new Error("Expected 1 auto complete element, '" + inputId + "', but found " + input.length);
            console.warn("Expected 1 auto complete element, '" + inputId + "', but found " + input.length);
            return;
        }

        // Hide the auto complete container and size it to the same size as the textbox.
        var $element = $(element);
        element.style.display = "none";
        element.style.position = "absolute";
        element.style.left = "auto";
        element.style.top = (input.height() + 20) + "px";

        //This makes elements with long names overflow the container... commenting it for the moment
        //element.style.width = input.width() + "px";

        // Clicking an element in the auto complete list should hide it.
        $element.on('click', () => setTimeout(() => element.style.display = "none", 0));

        // Leaving the textbox should hide the auto complete list.
        input.on('blur', (args: JQueryEventObject) => setTimeout(() => element.style.display = "none", 200));

        // Putting the focus back on the textbox should show the auto complete list if we have items.
        input.on('focus', (args: JQueryEventObject) => setTimeout(() =>
            element.style.display = this.getAllAutoCompleteItems($element).length > 0 ? "block" : "none"));

        // Up, down, enter all have special meaning.
        input.on('keydown', (args: JQueryEventObject) => this.handleKeyPress(element, $element, input, args));

        // When the results change and we have 1 or more, display the auto complete container.
        var results: KnockoutObservableArray<any> = allBindings()['foreach'];
        if (!results) {
            throw new Error("Unable to find results list for auto complete.");
        }
        var subscription = results.subscribe((array: any[]) => {
            element.style.display = array.length === 0 || !input.is(":focus") ? "none" : "block";
        });

        // Clean up after ourselves when the node is removed from the DOM.
        ko.utils.domNodeDisposal.addDisposeCallback(element, () => {
            input.off('blur');
            $element.off('click');
            input.off('keydown');
            subscription.dispose();
        });
    }

    getAllAutoCompleteItems(resultContainer: JQuery): JQuery {
        return resultContainer.children("li");
    }

    findAutoCompleteItemMatching(resultContainer: JQuery, text: string): HTMLLIElement {
        var textLower = text.toLowerCase();
        return this.getAllAutoCompleteItems(resultContainer)
            .toArray()
            .filter((el: HTMLLIElement) => el.textContent && el.textContent.trim().toLowerCase().indexOf(textLower) >= 0)[0];
    }

    handleKeyPress(element: HTMLElement, $element: JQuery, $input: JQuery, args: JQueryEventObject) {
        var enter = 13;
        var escape = 27;
        var downArrow = 40;
        var upArrow = 38;

        if (args.which === escape) {
            element.style.display = "none";
        }

        var lis: JQuery, curSelected: JQuery;
        if (element.style.display == "none" && args.which === downArrow) {
            if ($element.children("li").length > 0 && $input.is(":focus")) {
                setTimeout(() => element.style.display = "block", 0);
                return true;
            }
        }

        if (args.which === downArrow || args.which === upArrow || args.which === enter) {
            lis = this.getAllAutoCompleteItems($element);
            curSelected = $element.find(".active");
        }

        if (args.which === downArrow) {
            if (curSelected.length > 0) {
                curSelected.removeClass("active");
                var nextSelected = curSelected.next();

                if (nextSelected.length) {
                    nextSelected.addClass("active");
                    $element.scrollTop((nextSelected.index() - 1) * 30);
                } else {
                    lis.first().addClass("active");
                    $element.scrollTop(0);
                }

            } else {
                curSelected = lis.first().addClass("active");
            }
        } else if (args.which === upArrow) {
            if (curSelected.length > 0) {
                curSelected.removeClass("active");
                var prevSelected = curSelected.prev();

                if (prevSelected.length) {
                    prevSelected.addClass("active");
                    $element.scrollTop((prevSelected.index() - 1) * 30);
                } else {
                    lis.last().addClass("active");
                    $element.scrollTop($element.children("li").length * 30);
                }

            } else {
                curSelected = lis.last().addClass("active");
            }
        }
        else if (args.which === enter) {
            var itemToSelect = curSelected.length ? curSelected : $(this.findAutoCompleteItemMatching($element, $input.val()));
            if (itemToSelect.length) {
                itemToSelect.click();
            }
        }
    }
}

export = autoCompleteBindingHandler;
